using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class AgentCommandObservationTests
{
    [Fact]
    public async Task SuccessfulCommand_IsAvailableToOutputAcceptanceAsProcessExecution()
    {
        var observations = await RunAsync(SuccessCommand());

        observations
            .Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new ToolObservation(
                    "run_verification_1",
                    ToolEffect.ProcessExecution,
                    ToolEvidence.None
                )
            );
    }

    [Fact]
    public async Task FailedCommand_IsNotAvailableToOutputAcceptance()
    {
        var observations = await RunAsync(FailureCommand());

        observations.Should().BeEmpty();
    }

    [Fact]
    public async Task BlockedCommand_IsNotAvailableToOutputAcceptance()
    {
        var observations = await RunAsync(
            SuccessCommand(),
            (_, _, _) =>
                ValueTask.FromResult<ToolInterceptionResult?>(
                    new ToolInterceptionResult.Blocked("Command blocked.")
                )
        );

        observations.Should().BeEmpty();
    }

    [Fact]
    public async Task LaterCommandFailure_InvalidatesEarlierSuccessfulObservation()
    {
        var command = OperatingSystem.IsWindows()
            ? "if exist command-ran.marker (exit /b 7) else (type nul > command-ran.marker & exit /b 0)"
            : "if [ -f command-ran.marker ]; then exit 7; else touch command-ran.marker; exit 0; fi";

        var observations = await RunAsync(command, invokeTwice: true);

        observations.Should().BeEmpty();
    }

    private static async Task<IReadOnlySet<ToolObservation>> RunAsync(
        string command,
        ToolInterceptor<TestState>? interceptor = null,
        bool invokeTwice = false
    )
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"tandem-command-observation-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        try
        {
            IReadOnlySet<ToolObservation>? observations = null;
            var responses = new List<ChatResponse> { ToolCall("run_verification_1") };
            if (invokeTwice)
            {
                responses.Add(ToolCall("run_verification_1"));
            }
            responses.Add(
                new ChatResponse(new ChatMessage(ChatRole.Assistant, "accepted"))
                {
                    FinishReason = ChatFinishReason.Stop,
                }
            );
            var client = new ScriptedChatClient([.. responses]);
            var workspace = AgentWorkspace<TestState>.Define(
                _ => path,
                [AgentCommand.Define("run_verification_1", "Run verification.", command)]
            );
            var agent = Agent
                .Create<TestState>("agent", "Verify.", client)
                .UseHarness("Test harness.")
                .WithWorkspace(
                    workspace,
                    [AgentTools.Always<TestState>(workspace.Commands)],
                    interceptor
                )
                .WithMessage(_ => "Verify.")
                .WithOutput<TestState, AcceptedOutput>(
                    (response, state) =>
                        new StructuredOutputResult<TestState>(
                            new StructuredOutcome<TestState>(
                                "agent.success",
                                "Accepted.",
                                Json("{}")
                            ),
                            [],
                            response,
                            new AcceptedOutput()
                        )
                )
                .WithOutputAcceptance<TestState, AcceptedOutput>(
                    (observation, _) =>
                    {
                        observations = observation.Tools;
                        return ValueTask.CompletedTask;
                    }
                )
                .Build();
            var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
            var pipeline = Pipeline
                .Start(agent, "agent-command-observation")
                .Route(agent.Success, complete, "complete")
                .Build(complete);

            await new PipelineRunner().RunAsync(pipeline, new TestState());

            return observations ?? throw new InvalidOperationException("Output was not accepted.");
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static ChatResponse ToolCall(string name) =>
        new(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent("command-call", name, new Dictionary<string, object?>())]
            )
        )
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private static string SuccessCommand() => OperatingSystem.IsWindows() ? "exit /b 0" : "exit 0";

    private static string FailureCommand() => OperatingSystem.IsWindows() ? "exit /b 7" : "exit 7";

    private sealed record TestState;

    private sealed record AcceptedOutput;

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => Task.FromResult(_responses.Dequeue());

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            foreach (
                var update in (
                    await GetResponseAsync(messages, options, cancellationToken)
                ).ToChatResponseUpdates()
            )
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
