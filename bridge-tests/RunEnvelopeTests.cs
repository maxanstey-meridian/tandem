using System.Text.Json;
using Xunit;

namespace Tandem.Bridge;

public sealed class RunEnvelopeTests
{
    [Theory]
    [InlineData("completion", "succeeded")]
    [InlineData("failure", "failed")]
    public async Task A_completed_run_reports_its_outcome_state_and_summary(
        string terminal,
        string status
    )
    {
        var envelope = await RunAsync(
            Graph(terminal),
            (callback, state) =>
                Task.FromResult(callback == "work" ? Value("{\"count\":1}") : Value("unused"))
        );

        Assert.Equal(status, envelope.GetProperty("status").GetString());
        Assert.True(Guid.TryParse(envelope.GetProperty("runId").GetString(), out _));
        Assert.Equal(1, envelope.GetProperty("state").GetProperty("count").GetInt32());
        Assert.Equal("Ended.", envelope.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task A_callback_contract_failure_reports_its_boundary_and_problems()
    {
        var envelope = await RunAsync(
            Graph("completion"),
            (_, _) =>
                Task.FromResult(
                    JsonSerializer.Serialize(
                        new
                        {
                            succeeded = false,
                            error = new
                            {
                                name = "ContractValidationError",
                                message = "work output validation failed",
                                boundary = "work output",
                                problems = new[]
                                {
                                    new { path = "$.count", message = "Expected number" },
                                },
                            },
                        }
                    )
                )
        );

        Assert.Equal("contract", envelope.GetProperty("status").GetString());
        Assert.Equal("work output", envelope.GetProperty("boundary").GetString());
        var problem = Assert.Single(envelope.GetProperty("problems").EnumerateArray());
        Assert.Equal("$.count", problem.GetProperty("path").GetString());
        Assert.Equal("Expected number", problem.GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_invalid_registration_reports_a_registration_contract_failure()
    {
        var envelope = await RunAsync(
            Graph("completion").Replace("\"contractVersion\":10", "\"contractVersion\":9"),
            (_, _) => Task.FromResult(Value("unused"))
        );

        Assert.Equal("contract", envelope.GetProperty("status").GetString());
        Assert.Equal("registration contract", envelope.GetProperty("boundary").GetString());
        var problem = Assert.Single(envelope.GetProperty("problems").EnumerateArray());
        Assert.Equal("contractVersion", problem.GetProperty("path").GetString());
        Assert.Equal("must be 10; received 9.", problem.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_builder_rule_reports_a_registration_contract_failure()
    {
        var graph = Graph("completion")
            .Replace(
                "\"routes\":[{\"source\":\"work\",\"target\":\"end\",\"label\":\"ended\"}]",
                "\"routes\":[{\"source\":\"work\",\"target\":\"end\",\"label\":\"first\"},"
                    + "{\"source\":\"work\",\"target\":\"end\",\"label\":\"second\"}]"
            );

        var envelope = await RunAsync(graph, (_, _) => Task.FromResult(Value("unused")));

        Assert.Equal("contract", envelope.GetProperty("status").GetString());
        Assert.Equal("registration contract", envelope.GetProperty("boundary").GetString());
        var problem = Assert.Single(envelope.GetProperty("problems").EnumerateArray());
        Assert.Equal("routes[1]", problem.GetProperty("path").GetString());
        Assert.Contains("unconditional", problem.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_callback_failure_reports_a_faulted_run()
    {
        var envelope = await RunAsync(
            Graph("completion"),
            (_, _) =>
                Task.FromResult(
                    JsonSerializer.Serialize(
                        new
                        {
                            succeeded = false,
                            error = new { name = "Error", message = "callback exploded" },
                        }
                    )
                )
        );

        Assert.Equal("faulted", envelope.GetProperty("status").GetString());
        Assert.Contains("callback exploded", envelope.GetProperty("message").GetString());
    }

    [Fact]
    public async Task An_operation_cancelled_without_run_cancellation_reports_a_faulted_run()
    {
        var envelope = await RunAsync(
            Graph("completion"),
            (_, _) => Task.FromException<string>(new OperationCanceledException("Timed out."))
        );

        Assert.Equal("faulted", envelope.GetProperty("status").GetString());
        Assert.Contains("Timed out.", envelope.GetProperty("message").GetString());
    }

    public static TheoryData<string> Cancellations => ["operation", "task", "aggregate"];

    [Theory]
    [MemberData(nameof(Cancellations))]
    public async Task A_cancelled_run_reports_cancellation_however_the_callback_surfaces_it(
        string surfaced
    )
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<string> Work(CancellationToken token)
        {
            started.SetResult();
            var cancelled = new TaskCompletionSource<string>();
            await using var _ = token.Register(() => cancelled.SetCanceled(token));
            try
            {
                return await cancelled.Task;
            }
            catch (TaskCanceledException exception) when (surfaced == "operation")
            {
                throw new OperationCanceledException(exception.Message, token);
            }
            catch (TaskCanceledException exception) when (surfaced == "aggregate")
            {
                throw new AggregateException(exception);
            }
        }

        var run = RunAsync(
            Graph("completion"),
            (callback, token) => callback == "work" ? Work(token) : Task.FromResult(Value("")),
            cancellation.Token
        );
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();
        var envelope = await run;

        Assert.Equal("cancelled", envelope.GetProperty("status").GetString());
        Assert.True(Guid.TryParse(envelope.GetProperty("runId").GetString(), out _));
    }

    [Fact]
    public void Failures_inside_an_aggregate_are_classified_by_their_inner_exceptions()
    {
        var runId = Guid.CreateVersion7();
        var contract = new CallbackContractException("work output", [new("$", "Invalid.")]);

        Assert.IsType<RunContractViolated>(
            RunEnvelope.Ended(
                runId,
                new PipelineRunException(
                    "Workflow executor failed.",
                    new AggregateException(new InvalidOperationException("other"), contract)
                ),
                runCancelled: false
            )
        );
        Assert.IsType<RunCancelled>(
            RunEnvelope.Ended(
                runId,
                new AggregateException(new TaskCanceledException()),
                runCancelled: true
            )
        );
    }

    private static async Task<JsonElement> RunAsync(
        string definition,
        Func<string, CancellationToken, Task<string>> invokeAsync,
        CancellationToken cancellationToken = default
    )
    {
        var json = await NodePipelineBridge.RunRegisteredGraphAsync(
            definition,
            (callback, _, _) => callback == "summary" ? Value("Ended.") : Value(""),
            (callback, _, _, token) => invokeAsync(callback, token),
            cancellationToken
        );
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Graph(string terminal) =>
        $$"""
            {"contractVersion":10,"name":"envelope","start":"work","initialState":"{\"count\":0}",
             "nodes":[{"id":"work","kind":"stage","runCallback":"work"},
                      {"id":"end","kind":"{{terminal}}","summaryCallback":"summary"}],
             "routes":[{"source":"work","target":"end","label":"ended"}],"outputs":["end"]}
            """;

    private static string Value(string value) =>
        JsonSerializer.Serialize(new { succeeded = true, value });
}
