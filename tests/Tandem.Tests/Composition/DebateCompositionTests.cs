using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;
using Tandem.Infrastructure.Lifecycle;
using Tandem.Infrastructure.Projection;
using Tandem.Tests.Infrastructure;

namespace Tandem.Tests.Composition;

public sealed class DebateCompositionTests
{
    [Fact]
    public async Task Debate_UsesTypedState_RevisionLoop_McpVerdict_AndRuntimeBookkeeping()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var order = new List<string>();
        var proposerClient = new ScriptedChatClient(
            order,
            "proposer",
            Text("{\"text\":\"Initial case\"}"),
            Text("{\"text\":\"Revised case\"}")
        );
        var criticClient = new ScriptedChatClient(
            order,
            "critic",
            Text("{\"accepted\":false,\"critique\":\"Address the counterexample\"}"),
            Text("{\"accepted\":true,\"critique\":\"The revision resolves it\"}")
        );
        var judgeClient = new ScriptedChatClient(
            order,
            "judge",
            Tool(
                "verdict-1",
                "submit_verdict",
                new Dictionary<string, object?>
                {
                    ["verdict"] = "Affirmed",
                    ["reason"] = "The revised argument survived criticism.",
                }
            )
        );

        var workflow = BuildWorkflow(
            fixture,
            proposerClient,
            criticClient,
            judgeClient,
            out var graphIds
        );
        var input = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(fixture.RunId),
            new DebateState("Should typed composition own lifecycle state?", [], 0, null)
        );

        var output = await RunAsync(workflow, input);

        order.Should().Equal("proposer", "critic", "proposer", "critic", "judge");
        output.State.Round.Should().Be(2);
        output.State.Arguments.Select(argument => argument.Text).Should().Contain("Revised case");
        output
            .State.Verdict.Should()
            .Be(new Verdict("Affirmed", "The revised argument survived criticism."));
        output.Runtime.InvocationCounts.Should().ContainKeys("proposer", "critic", "judge");
        output.Runtime.InvocationCounts["proposer"].Should().Be(2);
        output.Runtime.InvocationCounts["critic"].Should().Be(2);
        output.Runtime.InvocationCounts["judge"].Should().Be(1);
        output.Runtime.AgentSessions.Should().ContainKeys("proposer", "critic", "judge");
        output.Runtime.AgentUsage.Should().ContainKeys("proposer", "critic", "judge");
        graphIds.Should().BeEquivalentTo("proposer", "critic", "judge", "complete");
        workflow.ReflectEdges().Keys.Should().BeEquivalentTo("proposer", "critic", "judge");
    }

    [Fact]
    public async Task InvalidStructuredOutput_FailsClosedWithoutChangingDebateState()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var client = new ScriptedChatClient(
            [],
            "proposer",
            Text("not json"),
            Text("still invalid")
        );
        var block = CreateStructuredBlock(
            "proposer",
            fixture,
            client,
            ParseProposal,
            "Propose an argument."
        );
        var input = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(fixture.RunId),
            new DebateState("Question", [], 0, null)
        );

        var output = await block.HandleAsync(
            input,
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.State.Should().Be(input.State);
        client.CallCount.Should().Be(2);
    }

    [Theory]
    [InlineData("{\"text\":null}")]
    [InlineData("{\"text\":\"   \"}")]
    public async Task SemanticallyInvalidProposal_FailsClosedWithoutChangingDebateState(string json)
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var client = new ScriptedChatClient([], "proposer", Text(json), Text(json));
        var block = CreateStructuredBlock(
            "proposer",
            fixture,
            client,
            ParseProposal,
            "Propose an argument."
        );
        var input = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(fixture.RunId),
            new DebateState("Question", [], 0, null)
        );

        var output = await block.HandleAsync(
            input,
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.State.Should().Be(input.State);
    }

    [Theory]
    [InlineData("{\"accepted\":true,\"critique\":null}")]
    [InlineData("{\"accepted\":false,\"critique\":\" \"}")]
    public async Task SemanticallyInvalidCritique_FailsClosedWithoutChangingDebateState(string json)
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var client = new ScriptedChatClient([], "critic", Text(json), Text(json));
        var block = CreateStructuredBlock("critic", fixture, client, ParseCritique, "Critique.");
        var input = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(fixture.RunId),
            new DebateState("Question", [new Argument("proposer", "Case")], 1, null)
        );

        var output = await block.HandleAsync(
            input,
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.State.Should().Be(input.State);
    }

    [Fact]
    public async Task AcceptedVerdictReceipt_ReplaySkipsModel_AndAppliesTransitionOnce()
    {
        using var fixture = await LifecycleFixture.CreateAsync();
        var input = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(fixture.RunId),
            new DebateState("Question", [], 1, null)
        );
        var invocationId = input.Runtime.NextInvocationId("judge");
        var payload = JsonSerializer.SerializeToElement(
            new { verdict = "Affirmed", reason = "Already accepted." }
        );
        await new LifecycleReceiptStore(fixture.TandemHome).WriteAsync(
            fixture.RunId,
            invocationId,
            "judge",
            "debate.verdict.submitted",
            "Verdict submitted: Affirmed",
            payload,
            CancellationToken.None
        );
        var client = new ScriptedChatClient([], "judge", Text("must not execute"));
        var judge = CreateJudge(fixture, client);

        var output = await judge.HandleAsync(
            input,
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        client.CallCount.Should().Be(0);
        output.State.Verdict.Should().Be(new Verdict("Affirmed", "Already accepted."));
        output.Runtime.InvocationCounts["judge"].Should().Be(1);
    }

    [Fact]
    public async Task ObservedExecutor_ReportsOutcomeFromDebateMessage()
    {
        var outcome = new BlockOutcome(
            "debate.proposed",
            "proposer",
            "Case",
            JsonSerializer.SerializeToElement(new { text = "Case" })
        );
        var output = new PipelineMessage<DebateState>(
            PipelineRuntime.Create(Guid.CreateVersion7()),
            new DebateState("Question", [], 0, null),
            outcome
        );
        var observer = new RecordingObserver();
        var executor = new ObservedExecutor<
            PipelineMessage<DebateState>,
            PipelineMessage<DebateState>
        >("proposer", new ReturningExecutor(output), observer);

        await executor.HandleAsync(output, new NoOpWorkflowContext(), CancellationToken.None);

        observer.Outcome.Should().BeSameAs(outcome);
    }

    private static Workflow BuildWorkflow(
        LifecycleFixture fixture,
        IChatClient proposerClient,
        IChatClient criticClient,
        IChatClient judgeClient,
        out IReadOnlyCollection<string> graphIds
    )
    {
        var proposer = CreateStructuredBlock(
            "proposer",
            fixture,
            proposerClient,
            ParseProposal,
            "Propose."
        );
        var critic = CreateStructuredBlock(
            "critic",
            fixture,
            criticClient,
            ParseCritique,
            "Critique."
        );
        var judge = CreateJudge(fixture, judgeClient);
        var complete = new DebateCompleteBlock();
        var proposerBinding = proposer.BindExecutor();
        var criticBinding = critic.BindExecutor();
        var judgeBinding = judge.BindExecutor();
        var completeBinding = complete.BindExecutor();
        var workflow = new WorkflowBuilder(proposerBinding)
            .WithName("debate-proof")
            .AddEdge<PipelineMessage<DebateState>>(
                proposerBinding,
                criticBinding,
                condition: null,
                idempotent: false
            )
            .AddEdge<PipelineMessage<DebateState>>(
                criticBinding,
                proposerBinding,
                message => message!.LatestOutcome?.Kind == "debate.revision.requested"
            )
            .AddEdge<PipelineMessage<DebateState>>(
                criticBinding,
                judgeBinding,
                message => message!.LatestOutcome?.Kind == "debate.critique.accepted"
            )
            .AddEdge<PipelineMessage<DebateState>>(
                judgeBinding,
                completeBinding,
                message => message!.LatestOutcome?.Kind == "debate.verdict.submitted"
            )
            .WithOutputFrom(completeBinding)
            .Build();
        graphIds = workflow.ReflectExecutors().Keys.ToArray();
        return workflow;
    }

    private static AgentBlock<DebateState> CreateStructuredBlock(
        string id,
        LifecycleFixture fixture,
        IChatClient client,
        StructuredOutputParser<DebateState> parser,
        string instructions
    ) =>
        new(
            new AgentBlockConfig<DebateState>(
                id,
                id,
                instructions,
                [],
                message => $"Question: {message.State.Question}; round: {message.State.Round}",
                _ => fixture.WorkspacePath,
                _ => false,
                StructuredOutput: parser
            ),
            client,
            fixture.TandemHome,
            fixture.TandemExePath,
            configureChatOptions: options =>
                options.ResponseFormat =
                    id == "proposer"
                        ? ChatResponseFormat.ForJsonSchema<ProposalTerminal>()
                        : ChatResponseFormat.ForJsonSchema<CritiqueTerminal>()
        );

    private static AgentBlock<DebateState> CreateJudge(
        LifecycleFixture fixture,
        IChatClient client
    ) =>
        new(
            new AgentBlockConfig<DebateState>(
                "judge",
                "judge",
                "Judge the debate and submit the verdict.",
                ["submit_verdict"],
                message => $"Judge: {message.State.Question}",
                _ => fixture.WorkspacePath,
                _ => false,
                ReceiptTransition: (state, kind, payload) =>
                    kind == "debate.verdict.submitted"
                        ? state with
                        {
                            Verdict = new Verdict(
                                payload.GetProperty("verdict").GetString()!,
                                payload.GetProperty("reason").GetString()!
                            ),
                        }
                        : state,
                McpServerName: "debate"
            ),
            client,
            fixture.TandemHome,
            fixture.TandemExePath
        );

    private static StructuredOutputResult<DebateState> ParseProposal(
        string text,
        PipelineMessage<DebateState> message
    ) =>
        Parse(
            text,
            root =>
            {
                var proposal = root.GetProperty("text").GetString();
                if (string.IsNullOrWhiteSpace(proposal))
                {
                    throw new InvalidOperationException("Proposal text must not be blank.");
                }
                var argument = new Argument("proposer", proposal);
                var state = message.State with
                {
                    Arguments = [.. message.State.Arguments, argument],
                    Round = message.State.Round + 1,
                };
                return new StructuredOutcome<DebateState>(
                    "debate.proposed",
                    argument.Text,
                    root,
                    state
                );
            }
        );

    private static StructuredOutputResult<DebateState> ParseCritique(
        string text,
        PipelineMessage<DebateState> message
    ) =>
        Parse(
            text,
            root =>
            {
                var accepted = root.GetProperty("accepted").GetBoolean();
                var critiqueText = root.GetProperty("critique").GetString();
                if (string.IsNullOrWhiteSpace(critiqueText))
                {
                    throw new InvalidOperationException("Critique must not be blank.");
                }
                var critique = new Argument("critic", critiqueText);
                return new StructuredOutcome<DebateState>(
                    accepted ? "debate.critique.accepted" : "debate.revision.requested",
                    critique.Text,
                    root,
                    message.State with
                    {
                        Arguments = [.. message.State.Arguments, critique],
                    }
                );
            }
        );

    private static StructuredOutputResult<DebateState> Parse(
        string text,
        Func<JsonElement, StructuredOutcome<DebateState>> map
    )
    {
        try
        {
            var root = JsonSerializer.Deserialize<JsonElement>(text);
            return new StructuredOutputResult<DebateState>(map(root), [], text, root);
        }
        catch (Exception exception)
            when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new StructuredOutputResult<DebateState>(
                null,
                [new StructuredOutputProblem("$", exception.Message)],
                text
            );
        }
    }

    private static async Task<PipelineMessage<DebateState>> RunAsync(
        Workflow workflow,
        PipelineMessage<DebateState> input
    )
    {
        await using var run = await InProcessExecution.RunStreamingAsync(
            workflow,
            input,
            input.Runtime.RunId.ToString("N"),
            CancellationToken.None
        );
        PipelineMessage<DebateState>? output = null;
        await foreach (var @event in run.WatchStreamAsync(CancellationToken.None))
        {
            if (
                @event is WorkflowOutputEvent workflowOutput
                && workflowOutput.Is<PipelineMessage<DebateState>>()
            )
            {
                output = workflowOutput.As<PipelineMessage<DebateState>>();
            }
            else if (@event is WorkflowErrorEvent error)
            {
                throw error.Exception ?? new InvalidOperationException("Debate workflow failed.");
            }
        }
        return output ?? throw new InvalidOperationException("Debate produced no output.");
    }

    private static ChatResponse Text(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]));

    private static ChatResponse Tool(
        string id,
        string name,
        IDictionary<string, object?> arguments
    ) =>
        new(new ChatMessage(ChatRole.Assistant, [new FunctionCallContent(id, name, arguments)]))
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private sealed class ScriptedChatClient(
        List<string> order,
        string name,
        params ChatResponse[] responses
    ) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);
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
            var response = _responses.Dequeue();
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class DebateCompleteBlock()
        : Executor<PipelineMessage<DebateState>, PipelineMessage<DebateState>>("complete")
    {
        public override ValueTask<PipelineMessage<DebateState>> HandleAsync(
            PipelineMessage<DebateState> message,
            IWorkflowContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(message);
    }

    private sealed class ReturningExecutor(PipelineMessage<DebateState> output)
        : Executor<PipelineMessage<DebateState>, PipelineMessage<DebateState>>("inner")
    {
        public override ValueTask<PipelineMessage<DebateState>> HandleAsync(
            PipelineMessage<DebateState> message,
            IWorkflowContext context,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(output);
    }

    private sealed class RecordingObserver : IBlockExecutionObserver
    {
        public BlockOutcome? Outcome { get; private set; }

        public ValueTask StartedAsync(string blockId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask CompletedAsync(
            string blockId,
            BlockOutcome? outcome,
            TimeSpan duration,
            CancellationToken cancellationToken
        )
        {
            Outcome = outcome;
            return ValueTask.CompletedTask;
        }
    }
}

public sealed record DebateState(
    string Question,
    IReadOnlyList<Argument> Arguments,
    int Round,
    Verdict? Verdict
);

public sealed record Argument(string Speaker, string Text);

public sealed record Verdict(string Value, string Reason);

public sealed record ProposalTerminal(string Text);

public sealed record CritiqueTerminal(bool Accepted, string Critique);
