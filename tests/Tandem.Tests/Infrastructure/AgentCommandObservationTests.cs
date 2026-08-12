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
        var observations = (await RunAsync(SuccessCommand())).Tools;

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
        var observations = (await RunAsync(FailureCommand())).Tools;

        observations.Should().BeEmpty();
    }

    [Fact]
    public async Task BlockedCommand_IsNotAvailableToOutputAcceptance()
    {
        var observations = (
            await RunAsync(
                SuccessCommand(),
                (_, _, _) =>
                    ValueTask.FromResult<ToolInterceptionResult?>(
                        new ToolInterceptionResult.Blocked("Command blocked.")
                    )
            )
        ).Tools;

        observations.Should().BeEmpty();
    }

    [Fact]
    public async Task LaterCommandFailure_InvalidatesEarlierSuccessfulObservation()
    {
        var command = OperatingSystem.IsWindows()
            ? "if exist command-ran.marker (exit /b 7) else (type nul > command-ran.marker & exit /b 0)"
            : "if [ -f command-ran.marker ]; then exit 7; else touch command-ran.marker; exit 0; fi";

        var observations = (await RunAsync(command, invokeTwice: true)).Tools;

        observations.Should().BeEmpty();
    }

    [Fact]
    public async Task SuccessfulObservation_SurvivesAnEarlierRejectedOutput()
    {
        var observations = (await RunAsync(SuccessCommand(), rejectFirstOutput: true)).Tools;

        observations
            .Should()
            .ContainSingle(observation => observation.Name == "run_verification_1");
    }

    [Fact]
    public async Task InvocationHistory_CapturesOwnedArgumentsAndSharesThemWithInterceptor()
    {
        JsonElement intercepted = default;

        var observation = await RunAsync(
            SuccessCommand(),
            (_, invocation, _) =>
            {
                intercepted = invocation.Arguments;
                return ValueTask.FromResult<ToolInterceptionResult?>(null);
            }
        );

        var invocation = observation.ToolInvocations.Should().ContainSingle().Subject;
        invocation.Arguments.ValueKind.Should().Be(JsonValueKind.Object);
        invocation.Arguments.EnumerateObject().Should().BeEmpty();
        intercepted.GetRawText().Should().Be(invocation.Arguments.GetRawText());
        invocation.Arguments.GetRawText().Should().Be("{}");
    }

    [Fact]
    public async Task CompletedAndFailedCommands_PreserveOrderedProcessEvidence()
    {
        var command = OperatingSystem.IsWindows()
            ? "if exist command-ran.marker (echo failed-out & echo failed-error 1>&2 & exit /b 7) else (echo passed-out & type nul > command-ran.marker & exit /b 0)"
            : "if [ -f command-ran.marker ]; then printf failed-out; printf failed-error >&2; exit 7; else printf passed-out; touch command-ran.marker; exit 0; fi";

        var observation = await RunAsync(command, invokeTwice: true);

        observation
            .ToolInvocations.Select(invocation => invocation.Status)
            .Should()
            .Equal(ToolInvocationStatus.Completed, ToolInvocationStatus.Failed);
        var completed = observation
            .ToolInvocations[0]
            .Result.Should()
            .BeOfType<ToolResultEvidence.Process>()
            .Subject;
        completed.ExitCode.Should().Be(0);
        completed.Stdout.Should().Contain("passed-out");
        completed.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        completed.TimedOut.Should().BeFalse();
        var failed = observation
            .ToolInvocations[1]
            .Result.Should()
            .BeOfType<ToolResultEvidence.Process>()
            .Subject;
        failed.ExitCode.Should().Be(7);
        failed.Stdout.Should().Contain("failed-out");
        failed.Stderr.Should().Contain("failed-error");
        observation.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task BlockedCommand_IsRecordedWithoutResultEvidence()
    {
        var observation = await RunAsync(
            SuccessCommand(),
            (_, _, _) =>
                ValueTask.FromResult<ToolInterceptionResult?>(
                    new ToolInterceptionResult.Blocked("Command blocked.")
                )
        );

        observation
            .ToolInvocations.Should()
            .ContainSingle()
            .Which.Should()
            .Match<ToolInvocationObservation>(invocation =>
                invocation.Status == ToolInvocationStatus.Blocked && invocation.Result == null
            );
    }

    [Fact]
    public async Task InvocationHistory_SurvivesStructuredOutputCorrection()
    {
        var observation = await RunAsync(SuccessCommand(), rejectFirstOutput: true);

        observation.ToolInvocations.Should().ContainSingle();
        observation.ToolInvocations[0].Status.Should().Be(ToolInvocationStatus.Completed);
        observation.Tools.Should().ContainSingle();
    }

    [Fact]
    public async Task Collector_snapshot_preserves_reserved_order_when_calls_complete_in_reverse()
    {
        var collector = new Tandem.Infrastructure.Blocks.ToolOutcomeCollector();
        var first = collector.ReserveToolInvocation();
        var second = collector.ReserveToolInvocation();
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var firstCompletion = Task.Run(async () =>
        {
            await releaseFirst.Task;
            collector.CompleteToolInvocation(first, Invocation("first"));
        });
        var secondCompletion = Task.Run(() =>
        {
            collector.CompleteToolInvocation(second, Invocation("second"));
            releaseFirst.SetResult();
        });

        await Task.WhenAll(firstCompletion, secondCompletion);

        collector
            .ToolInvocations.Select(invocation => invocation.Name)
            .Should()
            .Equal("first", "second");
    }

    [Fact]
    public async Task Later_reserved_failure_invalidates_earlier_success_despite_reverse_completion()
    {
        var collector = new Tandem.Infrastructure.Blocks.ToolOutcomeCollector();
        var first = collector.ReserveToolInvocation();
        var second = collector.ReserveToolInvocation();
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously
        );

        var firstCompletion = Task.Run(async () =>
        {
            await releaseFirst.Task;
            collector.RecordSuccessfulToolCall(
                first,
                new Tandem.Infrastructure.ToolObservationDescriptor("tool", null)
            );
        });
        var secondCompletion = Task.Run(() =>
        {
            collector.RecordFailedToolCall(second, "tool");
            releaseFirst.SetResult();
        });

        await Task.WhenAll(firstCompletion, secondCompletion);

        collector.SuccessfulTools.Should().BeEmpty();
    }

    private static Tandem.Infrastructure.ToolInvocationObservationDescriptor Invocation(
        string name
    ) => new(name, null, Json("{}"), Tandem.Infrastructure.ToolInvocationStatus.Completed, null);

    private static async Task<OutputAcceptanceObservation<TestState, AcceptedOutput>> RunAsync(
        string command,
        ToolInterceptor<TestState>? interceptor = null,
        bool invokeTwice = false,
        bool rejectFirstOutput = false
    )
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"tandem-command-observation-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(path);
        try
        {
            OutputAcceptanceObservation<TestState, AcceptedOutput>? acceptedObservation = null;
            var responses = new List<ChatResponse>();
            if (rejectFirstOutput)
            {
                responses.Add(TextResponse());
            }
            responses.Add(ToolCall("run_verification_1"));
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
            var acceptanceAttempt = 0;
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
                        acceptedObservation = observation;
                        return ValueTask.CompletedTask;
                    }
                )
                .RequireOutputAcceptance<TestState, AcceptedOutput>(_ =>
                    rejectFirstOutput && acceptanceAttempt++ == 0
                        ? [new StructuredOutputProblem("$", "Use the verification tool first.")]
                        : []
                )
                .Build();
            var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
            var pipeline = Pipeline
                .Start(agent, "agent-command-observation")
                .Route(agent.Success, complete, "complete")
                .Build(complete);

            await new PipelineRunner().RunAsync(pipeline, new TestState());

            return acceptedObservation
                ?? throw new InvalidOperationException("Output was not accepted.");
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

    private static ChatResponse TextResponse() =>
        new(new ChatMessage(ChatRole.Assistant, "accepted"))
        {
            FinishReason = ChatFinishReason.Stop,
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
