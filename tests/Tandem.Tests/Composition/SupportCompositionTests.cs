using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Domain;
using Tandem.Infrastructure;
using Tandem.Sample.Support;

namespace Tandem.Tests.Composition;

public sealed class SupportCompositionTests
{
    [Fact]
    public void StructuredTransitions_ValidateAndUpdateTypedState()
    {
        var input = Input();

        var classification = input.State.RecordClassification(
            new ClassificationDecision("billing")
        );
        var classified = classification;
        var resolution = classified.RecordResolution(
            new ResolutionDecision("The duplicate charge was reversed.")
        );

        classified.Category.Should().Be("billing");
        classified.AccountContext.Should().BeNull();
        resolution.ProposedResolution.Should().Contain("reversed");
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Classification_RejectsBlankCategory(string category)
    {
        var input = Input();

        var result = new ClassificationDecisionValidator().Validate(
            new ClassificationDecision(category)
        );

        result.IsValid.Should().BeFalse();
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
        inspection.StartStepId.Should().Be("support-classify");
        inspection
            .StepIds.Should()
            .BeEquivalentTo(
                "support-classify",
                LoadAccountStage.StepId,
                "support-resolve",
                SupportIds.CustomerReply,
                "support-close",
                "support-escalate",
                "support-failed"
            );
        inspection
            .OutputStepIds.Should()
            .Equal("support-close", "support-escalate", "support-failed");
        inspection.Routes.Should().HaveCount(6);
        Regex
            .Matches(inspection.Mermaid, Regex.Escape(SupportIds.CustomerReply))
            .Should()
            .HaveCount(1);
        Regex.Matches(inspection.Dot, Regex.Escape(SupportIds.CustomerReply)).Should().HaveCount(1);
        inspection.Mermaid.Should().NotContain("--request").And.NotContain("--resume");
        inspection.Dot.Should().NotContain("--request").And.NotContain("--resume");
        inspection.Mermaid.Should().Contain("-.->");
        inspection.Dot.Should().Contain("style=dashed");
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
        output.LatestOutcome!.StepId.Should().Be(terminalStep);
        output.LatestOutcome.Kind.Should().Be(StandardOutcomeKinds.Success);
        output.Runtime.AgentSessions.Should().ContainKeys("support-classify", "support-resolve");
        output.Runtime.AgentUsage.Should().ContainKeys("support-classify", "support-resolve");
        fixture.Lookup.ReceivedState!.Category.Should().Be("billing");
        fixture.Lookup.ReceivedState.AccountContext.Should().BeNull();
    }

    internal static PipelineMessage<SupportState> Input() =>
        new(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new SupportState("I was charged twice.", "customer-42")
        );

    internal static async Task<PipelineMessage<SupportState>> RunInProcessAsync(
        Pipeline<SupportState> pipeline,
        PipelineMessage<SupportState> input,
        CustomerReply reply
    )
    {
        return await new InProcessPipelineRunner().RunAsync(
            pipeline,
            input.Runtime.RunId,
            input.State,
            new SupportRequestHandler(reply),
            CancellationToken.None
        );
    }

    internal sealed class Fixture : IDisposable
    {
        private Fixture(
            string home,
            ServiceProvider provider,
            Pipeline<SupportState> pipeline,
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
        public Pipeline<SupportState> Pipeline { get; }
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
            services.AddSingleton<IAccountLookup>(lookup);
            services.AddCustomerSupport(
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

    private sealed class SupportRequestHandler(CustomerReply reply) : IExternalRequestHandler
    {
        public ValueTask<ExternalRequestAnswer> WaitAsync(
            PendingExternalRequest request,
            CancellationToken cancellationToken
        )
        {
            request.RequestType.Should().Be(typeof(CustomerQuestion));
            request.Value.Should().BeOfType<CustomerQuestion>();
            return ValueTask.FromResult(
                new ExternalRequestAnswer(
                    request.RunId,
                    request.RequestId,
                    typeof(CustomerReply),
                    reply
                )
            );
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
