using System.Runtime.CompilerServices;
using System.Text;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Infrastructure;

public sealed class RemediationTests
{
    [Fact]
    public async Task Raw_output_preserves_instructions_and_corrects_in_the_requested_format()
    {
        using var client = new ReplyClient("bad", "accepted");
        var agent = Agent
            .Create<string>("raw", "Follow the output instructions.", client)
            .WithMessage(_ => "Decide.")
            .WithRawOutput(new RawDefinition(), (_, value) => value)
            .Build();
        var result = await new PipelineRunner().RunAsync(
            Pipeline.Start(agent, "raw-test").Build(agent),
            "initial"
        );
        result.State.Should().Be("accepted");
        client.Requests.Should().HaveCount(2);
        client.Requests[0].Should().Contain("Return the single word accepted.");
        client.Requests[1].Should().Contain("corrected response in the requested format");
        client.Requests[1].Should().NotContain("corrected JSON object");
        client.Formats.Should().OnlyContain(format => format == null);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]
    [InlineData(65, true)]
    [InlineData(4097, true)]
    public async Task Paging_reconstructs_a_long_line_after_short_lines(
        int prefixLength,
        bool utf16
    )
    {
        var path = Path.GetTempFileName();
        try
        {
            var expected = new[]
            {
                new string('s', prefixLength),
                new string('x', 65000) + "😀" + new string('y', 115000),
                "last",
            };
            await File.WriteAllTextAsync(
                path,
                string.Join("\r\n", expected),
                utf16 ? Encoding.Unicode : new UTF8Encoding(false)
            );
            var actual = new Dictionary<int, StringBuilder>();
            var line = 1;
            var offset = 0;
            for (var pageNumber = 0; pageNumber < 20; pageNumber++)
            {
                var page = await BoundedLinePageReader.ReadAsync(
                    path,
                    line,
                    pageNumber % 2 == 0 ? 200 : 1,
                    offset
                );
                for (var index = 0; index < page.Lines.Count; index++)
                {
                    var key = page.StartLine + index;
                    if (!actual.TryGetValue(key, out var text))
                    {
                        actual[key] = text = new StringBuilder();
                    }
                    text.Append(page.Lines[index]);
                }
                if (page.NextStartLine is null)
                {
                    break;
                }
                line = page.NextStartLine.Value;
                offset = page.NextCharacterOffset ?? 0;
            }
            actual
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value.ToString())
                .Should()
                .Equal(expected);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(7999, false, false)]
    [InlineData(8000, false, false)]
    [InlineData(9000, true, false)]
    [InlineData(9000, true, true)]
    public async Task Acceptance_marks_shortened_process_evidence(
        int length,
        bool truncated,
        bool stderr
    )
    {
        using var client = new ReplyClient("accepted") { Command = "capture" };
        var command = OperatingSystem.IsWindows()
            ? $"[Console]::{(stderr ? "Error" : "Out")}.Write('0' * {length})"
            : $"printf '%0{length}d' 0{(stderr ? " >&2" : "")}";
        var workspace = AgentWorkspace<string>.Define(
            _ => Path.GetTempPath(),
            [AgentCommand.Define("capture", "Print output.", command)]
        );
        ToolResultEvidence.Process? captured = null;
        var agent = Agent
            .Create<string>("worker", "Use capture.", client)
            .UseHarness("Use the command.")
            .WithWorkspace(workspace, [AgentTools.Always<string>(workspace.Commands)])
            .WithMessage(_ => "Run.")
            .WithRawOutput(new RawDefinition(), (_, value) => value)
            .WithOutputAcceptance<string, string>(
                (observation, _) =>
                {
                    captured = (ToolResultEvidence.Process)
                        observation.ToolInvocations.Single().Result!;
                    return ValueTask.CompletedTask;
                }
            )
            .Build();
        await new PipelineRunner().RunAsync(
            Pipeline.Start(agent, "evidence-test").Build(agent),
            "initial"
        );
        captured.Should().NotBeNull();
        (stderr ? captured!.Stderr : captured!.Stdout).Length.Should().Be(Math.Min(length, 8000));
        captured.Truncated.Should().Be(truncated);
    }

    [Fact]
    public void Raw_parser_preserves_cancellation_and_unexpected_faults()
    {
        var validator = new InlineValidator<string>();
        Action cancel = () =>
            AgentStructuredOutputPolicy.ParseRaw<string, string>(
                "text",
                _ => throw new OperationCanceledException(),
                validator
            );
        cancel.Should().Throw<OperationCanceledException>();
        Action fault = () =>
            AgentStructuredOutputPolicy.ParseRaw<string, string>(
                "text",
                _ => throw new IOException("broken adapter"),
                validator
            );
        fault.Should().Throw<IOException>();
    }

    [Fact]
    public void Raw_output_rejects_intrinsic_errors_before_contextual_validation()
    {
        var intrinsic = new InlineValidator<string>();
        intrinsic.RuleFor(value => value).NotEmpty();
        var contextual = new InlineValidator<string>();
        var called = false;
        contextual.RuleFor(value => value).Custom((_, _) => called = true);
        var result = AgentStructuredOutputPolicy.ParseRaw<string, string>(
            "text",
            _ => "",
            intrinsic,
            contextual
        );
        result.Success.Should().BeFalse();
        called.Should().BeFalse();
    }

    private sealed class RawDefinition : IAgentRawOutputDefinition<string, string>
    {
        public string Instructions => "Return the single word accepted.";
        public IValidator<string> Validator { get; } = new InlineValidator<string>();

        public string Parse(string response) =>
            response == "bad"
                ? throw new InvalidOperationException("The word must be accepted.")
                : response;
    }

    private sealed class ReplyClient(params string[] replies) : IChatClient
    {
        private int _reply;
        private bool _called;
        public string? Command { get; init; }
        public List<string> Requests { get; } = [];
        public List<ChatResponseFormat?> Formats { get; } = [];

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(
                (options?.Instructions ?? "")
                    + string.Join("\n", messages.Select(message => message.Text))
            );
            Formats.Add(options?.ResponseFormat);
            if (Command is not null && !_called)
            {
                _called = true;
                return Task.FromResult(
                    new ChatResponse(
                        new ChatMessage(
                            ChatRole.Assistant,
                            [
                                new FunctionCallContent(
                                    "call",
                                    Command,
                                    new Dictionary<string, object?>()
                                ),
                            ]
                        )
                    )
                    {
                        FinishReason = ChatFinishReason.ToolCalls,
                    }
                );
            }
            return Task.FromResult(
                new ChatResponse(
                    new ChatMessage(
                        ChatRole.Assistant,
                        replies[Math.Min(_reply++, replies.Length - 1)]
                    )
                )
                {
                    FinishReason = ChatFinishReason.Stop,
                }
            );
        }

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
