using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Actions;
using Tandem.Domain;
using Tandem.Sample.Debate;
using Tandem.Tests.Infrastructure;

namespace Tandem.Tests.Composition;

public sealed class DebateCompositionTests
{
    [Fact]
    public async Task Debate_ExecutesRevisionLoopAndReceiptReplayThroughPublicAuthoringSurface()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var clients = ScriptedClients.Create();
        var pipeline = Build(fixture, clients);
        var input = Input(fixture);
        await WriteVerdictReceiptAsync(fixture, input);

        var output = await RunAsync(pipeline, input);

        clients.Order.Should().Equal("proposer", "critic", "proposer", "critic");
        clients
            .Judge.CallCount.Should()
            .Be(0, "the accepted receipt is replayed before a model call");
        output.State.Round.Should().Be(2);
        output.State.Arguments.Select(argument => argument.Text).Should().Contain("Revised case");
        output.State.Verdict.Should().Be(new DebateVerdict("Affirmed", "Already accepted."));
        output.Runtime.InvocationCounts.Should().ContainKeys("proposer", "critic", "judge");
        output.Runtime.InvocationCounts["proposer"].Should().Be(2);
        output.Runtime.InvocationCounts["critic"].Should().Be(2);
        output.Runtime.InvocationCounts["judge"].Should().Be(1);
        output.Runtime.AgentSessions.Should().ContainKeys("proposer", "critic");
        output.Runtime.AgentSessions.Should().NotContainKey("judge");
        output.Runtime.AgentUsage.Should().ContainKeys("proposer", "critic");
        output.Runtime.AgentUsage.Should().NotContainKey("judge");
        output.Runtime.AgentProfiles.Should().ContainKeys("proposer", "critic");
        output.Runtime.AgentProfiles.Should().NotContainKey("judge");
    }

    [Fact]
    public async Task Debate_InspectionAndSerializationExposeOnlyPublicSemanticData()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var pipeline = Build(fixture, ScriptedClients.Create());
        var inspection = pipeline.Inspect();
        var input = Input(fixture);
        var json = JsonSerializer.Serialize(input);
        var roundTrip = JsonSerializer.Deserialize<PipelineMessage<DebateState>>(json);

        inspection.Name.Should().Be("debate");
        inspection.StartStepId.Should().Be("open");
        inspection
            .StepIds.Should()
            .BeEquivalentTo("open", "proposer", "critic", "judge", "complete", "debate-failed");
        inspection.Ports.Should().BeEmpty();
        inspection.OutputStepIds.Should().Equal("complete", "debate-failed");
        inspection.Routes.Should().HaveCount(6);
        inspection
            .Routes.Should()
            .OnlyContain(route =>
                inspection.StepIds.Contains(route.SourceId)
                && inspection.StepIds.Contains(route.TargetId)
            );
        inspection.Routes.Count(route => route.Conditional).Should().Be(5);
        inspection.Routes.Count(route => !route.Conditional).Should().Be(1);
        inspection.Mermaid.Should().StartWith("flowchart").And.Contain("revision requested");
        inspection.Dot.Should().StartWith("digraph");
        roundTrip.Should().BeEquivalentTo(input);
    }

    [Fact]
    public async Task AddDebate_RegistersCompositionAndActionSetExplicitly()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var clients = ScriptedClients.Create();
        var services = new ServiceCollection();
        services.AddSingleton(new TandemEnvironment(fixture.TandemHome, fixture.TandemExePath));
        services.AddTandem().AddDebate(Options(clients));
        await using var provider = services.BuildServiceProvider();

        provider
            .GetRequiredService<DebateComposition>()
            .Build()
            .Inspect()
            .Name.Should()
            .Be("debate");
        provider
            .GetRequiredService<LifecycleActionSetRegistry>()
            .Register("debate", new ServiceCollection())
            .Should()
            .NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ProposalTransition_RejectsBlankText(string text)
    {
        var input = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new DebateState("Question", [], 0, null)
        );

        var result = new ProposalDecisionValidator().Validate(new ProposalDecision(text));

        result.IsValid.Should().BeFalse();
        input.State.Round.Should().Be(0);
        input.State.Arguments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void CritiqueTransition_RejectsBlankCritique(string critique)
    {
        var input = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new DebateState("Question", [new DebateArgument("proposer", "Case")], 1, null)
        );

        var result = new CritiqueDecisionValidator().Validate(
            new CritiqueDecision(false, critique)
        );

        result.IsValid.Should().BeFalse();
        input.State.Arguments.Should().ContainSingle();
    }

    internal static Pipeline Build(LifecycleFixture fixture, ScriptedClients clients)
    {
        var services = new ServiceCollection();
        services.AddSingleton(new TandemEnvironment(fixture.TandemHome, fixture.TandemExePath));
        services.AddTandem().AddDebate(Options(clients));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<DebateComposition>().Build();
    }

    internal static PipelineMessage<DebateState> Input(LifecycleFixture fixture) =>
        new(
            PipelineRuntime.Create(fixture.RunId),
            new DebateState("Should typed composition own lifecycle state?", [], 0, null)
        );

    internal static async Task WriteVerdictReceiptAsync(
        LifecycleFixture fixture,
        PipelineMessage<DebateState> input
    ) =>
        await new LifecycleReceiptStore(fixture.TandemHome).WriteAsync(
            fixture.RunId,
            input.Runtime.NextInvocationId("judge"),
            "judge",
            "capability:Tandem.Sample.Debate.DebateState:submit_verdict",
            "Verdict submitted: Affirmed",
            JsonSerializer.SerializeToElement(
                new SubmitVerdict("Affirmed", "Already accepted."),
                JsonSerializerOptions.Web
            ),
            CancellationToken.None
        );

    private static DebateOptions Options(ScriptedClients clients) =>
        new(clients.Proposer, clients.Critic, clients.Judge);

    private static async Task<PipelineMessage<DebateState>> RunAsync(
        Pipeline pipeline,
        PipelineMessage<DebateState> input
    )
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            PipelineMafBridge.GetWorkflow(pipeline),
            input,
            input.Runtime.RunId.ToString("N"),
            CancellationToken.None
        );
        PipelineMessage<DebateState>? output = null;
        await foreach (var evt in run.WatchStreamAsync(CancellationToken.None))
        {
            if (
                evt is WorkflowOutputEvent workflowOutput
                && workflowOutput.Is<PipelineMessage<DebateState>>()
            )
            {
                output = workflowOutput.As<PipelineMessage<DebateState>>();
            }
            else if (evt is WorkflowErrorEvent error)
            {
                throw error.Exception ?? new InvalidOperationException("Debate workflow failed.");
            }
            else if (evt is ExecutorFailedEvent failed)
            {
                throw failed.Data ?? new InvalidOperationException("Debate executor failed.");
            }
        }
        return output ?? throw new InvalidOperationException("Debate produced no output.");
    }

    internal sealed class ScriptedClients
    {
        private ScriptedClients(
            List<string> order,
            ScriptedChatClient proposer,
            ScriptedChatClient critic,
            ScriptedChatClient judge
        )
        {
            Order = order;
            Proposer = proposer;
            Critic = critic;
            Judge = judge;
        }

        public List<string> Order { get; }
        public ScriptedChatClient Proposer { get; }
        public ScriptedChatClient Critic { get; }
        public ScriptedChatClient Judge { get; }

        public static ScriptedClients Create()
        {
            var order = new List<string>();
            return new ScriptedClients(
                order,
                new ScriptedChatClient(
                    order,
                    "proposer",
                    "{\"text\":\"Initial case\"}",
                    "{\"text\":\"Revised case\"}"
                ),
                new ScriptedChatClient(
                    order,
                    "critic",
                    "{\"accepted\":false,\"critique\":\"Revise\"}",
                    "{\"accepted\":true,\"critique\":\"Accepted\"}"
                ),
                new ScriptedChatClient(order, "judge", "must not execute")
            );
        }
    }

    internal sealed class ScriptedChatClient(
        List<string> order,
        string name,
        params string[] responses
    ) : IChatClient
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
            order.Add(name);
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
