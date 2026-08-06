using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Tests.Infrastructure;

public sealed class StructuredOutputTests
{
    [Fact]
    public async Task InvalidPlannerDecision_IsCorrectedInSameSession_BeforeAuthorizingMutation()
    {
        using var directory = new TemporaryDirectory();
        var client = new ScriptedChatClient(
            Response(
                "{\"decision\":\"Proceed\",\"rationale\":\"Proceed.\","
                    + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                    + "\"humanQuestion\":\"N/A\"}"
            ),
            Response(
                "{\"decision\":\"Proceed\",\"rationale\":\"Proceed.\","
                    + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                    + "\"humanQuestion\":null}"
            )
        );
        var block = CreatePlannerBlock(directory.Path, client);
        var context = CreateContext(directory.Path);

        var output = await block.HandleAsync(
            new PipelineMessage(context),
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be(OutcomeKinds.PlannerProceed);
        output.Context.MutationAuthorized.Should().BeTrue();
        client.CallCount.Should().Be(2);
        client
            .Requests[1]
            .SelectMany(message => message.Contents)
            .OfType<TextContent>()
            .Select(content => content.Text)
            .Should()
            .Contain(text => text.Contains("humanQuestion") && text.Contains("must be empty"));
    }

    [Fact]
    public async Task SecondInvalidPlannerDecision_FailsClosedWithoutAuthorizingMutation()
    {
        using var directory = new TemporaryDirectory();
        var invalid = Response(
            "{\"decision\":\"Proceed\",\"rationale\":\"Proceed.\","
                + "\"constraints\":[],\"evidenceUsed\":[\"README.md\"],"
                + "\"humanQuestion\":\"N/A\"}"
        );
        var client = new ScriptedChatClient(invalid, invalid);
        var block = CreatePlannerBlock(directory.Path, client);

        var output = await block.HandleAsync(
            new PipelineMessage(CreateContext(directory.Path)),
            new NoOpWorkflowContext(),
            CancellationToken.None
        );

        output.LatestOutcome!.Kind.Should().Be("agent.failed");
        output.Context.MutationAuthorized.Should().BeFalse();
        output.LatestOutcome.Payload.GetProperty("rawResponse").GetString().Should().Contain("N/A");
    }

    [Fact]
    public void JsonExtractor_IgnoresBracesInsideStrings()
    {
        var json = StructuredJsonExtractor.Extract("prefix {\"rationale\":\"Use {value}\"} suffix");

        json.Should().Be("{\"rationale\":\"Use {value}\"}");
    }

    private static AgentBlock CreatePlannerBlock(string tandemHome, IChatClient client) =>
        new(
            new AgentBlockConfig(
                BlockIds.Planner,
                "planning",
                "Return a planner decision.",
                WorkspaceAccess.ReadOnly,
                [],
                StructuredOutput: PlannerDecisionPolicy.Parse
            ),
            client,
            tandemHome
        );

    private static PipelineContext CreateContext(string workspacePath)
    {
        var packet = new Packet(
            "structured-output",
            workspacePath,
            "main",
            [new Outcome("outcome", "Do the thing.")],
            [],
            [],
            ""
        );
        return PipelineContext.Create(Guid.CreateVersion7(), packet, "abc123", workspacePath);
    }

    private static ChatResponse Response(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public int CallCount { get; private set; }
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

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
            Requests.Add(messages.ToArray());
            CallCount++;
            foreach (var update in _responses.Dequeue().ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-structured-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
