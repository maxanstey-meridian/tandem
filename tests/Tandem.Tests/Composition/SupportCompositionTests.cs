using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.Agents.AI.Workflows;
using Microsoft.DurableTask.Client;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Domain;
using Tandem.Sample.Support;
using Tandem.Tests.Durable;

namespace Tandem.Tests.Composition;

public sealed class SupportCompositionTests
{
    [Fact]
    public void StructuredTransitions_ValidateAndUpdateTypedState()
    {
        var input = Input();

        var classification = SupportPolicies.ParseClassification(
            "{\"category\":\"billing\"}",
            input
        );
        var classified = classification.Outcome!.UpdatedState!;
        var resolution = SupportPolicies.ParseResolution(
            "{\"proposedResolution\":\"The duplicate charge was reversed.\"}",
            input with
            {
                State = classified,
            }
        );

        classification.Success.Should().BeTrue();
        classified.Category.Should().Be("billing");
        classified.AccountContext.Should().BeNull();
        resolution.Success.Should().BeTrue();
        resolution.Outcome!.UpdatedState!.ProposedResolution.Should().Contain("reversed");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"category\":\" \"}")]
    [InlineData("{\"wrong\":\"billing\"}")]
    public void Classification_FailsClosedWithoutMutatingState(string response)
    {
        var input = Input();

        var result = SupportPolicies.ParseClassification(response, input);

        result.Success.Should().BeFalse();
        result.Outcome.Should().BeNull();
        input.State.Category.Should().BeNull();
    }

    [Fact]
    public async Task InspectionSerializationAndRegistration_ExposeCompletePublicGraph()
    {
        using var fixture = Fixture.Create();
        var inspection = fixture.Pipeline.Inspect();
        var input = Input();
        var roundTrip = JsonSerializer.Deserialize<PipelineMessage<SupportState>>(
            JsonSerializer.Serialize(input)
        );

        inspection.Name.Should().Be("customer-support");
        inspection.StartStepId.Should().Be(ClassifyTicketAgent.StepId);
        inspection
            .StepIds.Should()
            .BeEquivalentTo(
                ClassifyTicketAgent.StepId,
                LoadAccountStage.StepId,
                ResolveTicketAgent.StepId,
                SupportIds.AskCustomer,
                SupportIds.CustomerReply,
                SupportIds.ApplyReply,
                CloseTicketStage.StepId,
                EscalateTicketStage.StepId
            );
        inspection.Ports.Should().ContainSingle(port => port.Id == SupportIds.CustomerReply);
        inspection
            .OutputStepIds.Should()
            .Equal(CloseTicketStage.StepId, EscalateTicketStage.StepId);
        inspection.Routes.Should().HaveCount(7);
        roundTrip.Should().BeEquivalentTo(input);
        fixture.Provider.GetRequiredService<SupportComposition>().Should().NotBeNull();
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(true, "closed", "support-close")]
    [InlineData(false, "escalated", "support-escalate")]
    public async Task CompletePipeline_ExecutesInProcessThroughRealRequestPort(
        bool resolved,
        string disposition,
        string terminalStep
    )
    {
        using var fixture = Fixture.Create();

        var output = await RunInProcessAsync(
            fixture.Pipeline,
            Input(),
            new CustomerReply(resolved ? "That fixed it." : "I am still blocked.", resolved)
        );

        output.State.Category.Should().Be("billing");
        output.State.AccountContext.Should().Be("Customer is active; duplicate charge is pending.");
        output.State.ProposedResolution.Should().Contain("reversed");
        output.State.FinalDisposition.Should().Be(disposition);
        output.LatestResult!.StepId.Should().Be(terminalStep);
        fixture.Lookup.ReceivedState!.Category.Should().Be("billing");
        fixture.Lookup.ReceivedState.AccountContext.Should().BeNull();
    }

    internal static PipelineMessage<SupportState> Input() =>
        new(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new SupportState("I was charged twice.", "customer-42")
        );

    internal static async Task<PipelineMessage<SupportState>> RunInProcessAsync(
        Pipeline pipeline,
        PipelineMessage<SupportState> input,
        CustomerReply reply
    )
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            PipelineMafBridge.GetWorkflow(pipeline),
            input,
            input.Runtime.RunId.ToString("N"),
            CancellationToken.None
        );
        PipelineMessage<SupportState>? output = null;
        await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is RequestInfoEvent request)
            {
                request.Request.IsDataOfType<CustomerQuestion>().Should().BeTrue();
                await run.SendResponseAsync(request.Request.CreateResponse(reply));
            }
            else if (
                evt is WorkflowOutputEvent workflowOutput
                && workflowOutput.Is<PipelineMessage<SupportState>>()
            )
            {
                output = workflowOutput.As<PipelineMessage<SupportState>>();
            }
            else if (evt is WorkflowErrorEvent error)
            {
                throw error.Exception ?? new InvalidOperationException("Support workflow failed.");
            }
            else if (evt is ExecutorFailedEvent failed)
            {
                throw failed.Data ?? new InvalidOperationException("Support executor failed.");
            }
        }
        return output ?? throw new InvalidOperationException("Support produced no output.");
    }

    internal sealed class Fixture : IDisposable
    {
        private Fixture(
            string home,
            ServiceProvider provider,
            Pipeline pipeline,
            RecordingAccountLookup lookup
        )
        {
            Home = home;
            Provider = provider;
            Pipeline = pipeline;
            Lookup = lookup;
        }

        public string Home { get; }
        public ServiceProvider Provider { get; }
        public Pipeline Pipeline { get; }
        public RecordingAccountLookup Lookup { get; }

        public static Fixture Create()
        {
            var home = Path.Combine(
                Path.GetTempPath(),
                "tandem-support-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(home);
            var lookup = new RecordingAccountLookup();
            var services = new ServiceCollection();
            services.AddSingleton(new TandemEnvironment(home));
            services.AddSingleton<IAccountLookup>(lookup);
            services
                .AddTandem()
                .AddCustomerSupport(
                    new SupportOptions(
                        new ScriptedChatClient("{\"category\":\"billing\"}"),
                        new ScriptedChatClient(
                            "{\"proposedResolution\":\"The duplicate charge was reversed.\"}"
                        )
                    )
                );
            var provider = services.BuildServiceProvider();
            return new Fixture(
                home,
                provider,
                provider.GetRequiredService<SupportComposition>().Build(),
                lookup
            );
        }

        public void Dispose()
        {
            Provider.Dispose();
            if (Directory.Exists(Home))
            {
                Directory.Delete(Home, recursive: true);
            }
        }
    }

    internal sealed class RecordingAccountLookup : IAccountLookup
    {
        public SupportState? ReceivedState { get; private set; }

        public ValueTask<string> LoadAsync(SupportState state, CancellationToken cancellationToken)
        {
            ReceivedState = state;
            return ValueTask.FromResult("Customer is active; duplicate charge is pending.");
        }
    }

    internal sealed class ScriptedChatClient(params string[] responses) : IChatClient
    {
        private readonly Queue<string> _responses = new(responses);
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            var response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [new TextContent(_responses.Dequeue())])
            );
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}

[Collection("Durable Task Scheduler")]
public sealed class SupportDurableCompositionTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public async Task Support_SuspendsResumesAndCompletesAsClosedGenericMessage()
    {
        DtsFixture.EnsureReachable();
        using var fixture = SupportCompositionTests.Fixture.Create();
        var input = SupportCompositionTests.Input();
        var workflow = PipelineMafBridge.GetWorkflow(fixture.Pipeline);
        var runId = "support-durable-" + Guid.NewGuid().ToString("N");

        await using var host = await DurableHost.StartAsync(options =>
            options.AddWorkflow(workflow)
        );
        var run = (IAwaitableWorkflowRun)await host.WorkflowClient.RunAsync(workflow, input, runId);
        for (var i = 0; i < 60 && fixture.Lookup.ReceivedState is null; i++)
        {
            await Task.Delay(250, CancellationToken.None);
        }

        fixture.Lookup.ReceivedState.Should().NotBeNull("execution must reach the request port");
        var pending = await host.DurableTaskClient.GetInstanceAsync(
            runId,
            getInputsAndOutputs: false,
            CancellationToken.None
        );
        pending.Should().NotBeNull();

        await host.DurableTaskClient.RaiseEventAsync(
            runId,
            SupportIds.CustomerReply,
            JsonSerializer.Serialize(new CustomerReply("Confirmed fixed.", true), _jsonOptions),
            CancellationToken.None
        );
        var output = await run.WaitForCompletionAsync<PipelineMessage<SupportState>>();
        var completed = await host.DurableTaskClient.WaitForInstanceCompletionAsync(
            runId,
            getInputsAndOutputs: true,
            CancellationToken.None
        );

        output.Should().NotBeNull();
        output!.GetType().Should().Be<PipelineMessage<SupportState>>();
        output.State.CustomerId.Should().Be(input.State.CustomerId);
        output.State.CustomerReply.Should().Be("Confirmed fixed.");
        output.State.FinalDisposition.Should().Be("closed");
        output.LatestResult!.StepId.Should().Be(CloseTicketStage.StepId);
        completed!.RuntimeStatus.Should().Be(OrchestrationRuntimeStatus.Completed);
    }
}
