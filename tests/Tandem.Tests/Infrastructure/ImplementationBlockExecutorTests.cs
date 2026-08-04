using System.Diagnostics;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class ImplementationBlockExecutorTests
{
    private static readonly string _gitPath =
        Environment.GetEnvironmentVariable("TANDEM_TEST_GIT") ?? "git";

    [Fact]
    public async Task Execute_WritesGreetingAndReturnsBlockResult()
    {
        using var source = TempSourceRepo.Create();
        using var run = TempRunDir.Create();
        var workspacePath = Path.Combine(run.Dir, "workspace");
        Directory.CreateDirectory(workspacePath);

        var packet = MakePacket(source.Path);
        var profile = MakeProfile();
        var context = new RunContext(
            Guid.CreateVersion7(),
            packet,
            source.HeadSha,
            workspacePath,
            profile
        );

        var script = new ScriptedChatClient(
            MakeToolCallResponse(
                "call-1",
                "file_access_write",
                new Dictionary<string, object?>
                {
                    ["fileName"] = "greeting.txt",
                    ["content"] = "Hello from Tandem.",
                }
            ),
            MakeTextResponse("I created greeting.txt with the requested content.")
        );

        var executor = new ImplementationBlockExecutor(_ => script);
        var binding = executor.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();

        var sessionId = context.RunId.ToString("N");
        await using var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            context,
            sessionId,
            CancellationToken.None
        );

        var events = new List<WorkflowEvent>();
        BlockResult? result = null;
        Exception? failure = null;

        await foreach (var evt in runHandle.WatchStreamAsync(CancellationToken.None))
        {
            events.Add(evt);

            if (evt is WorkflowErrorEvent errorEvent)
            {
                failure = errorEvent.Exception;
            }
            else if (evt is ExecutorFailedEvent failedEvent)
            {
                failure = failedEvent.Data;
            }
            else if (evt is WorkflowOutputEvent output && output.Is<BlockResult>())
            {
                result = output.As<BlockResult>();
            }
        }

        failure.Should().BeNull("the workflow should not fail");
        result.Should().NotBeNull("the workflow must produce a block result");
        result!.FinalResponse.Should().Contain("greeting.txt");
        result.ModelId.Should().Be(profile.Model);
        result.WorkspacePath.Should().Be(workspacePath);

        File.Exists(Path.Combine(workspacePath, "greeting.txt")).Should().BeTrue();
        File.ReadAllText(Path.Combine(workspacePath, "greeting.txt"))
            .Should()
            .Be("Hello from Tandem.");

        events.OfType<AgentResponseUpdateEvent>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Execute_RejectsGitConfigWriteWhileAllowingNormalWrite()
    {
        using var source = TempSourceRepo.Create();
        using var run = TempRunDir.Create();
        var workspacePath = Path.Combine(run.Dir, "workspace");
        Directory.CreateDirectory(workspacePath);

        var packet = MakePacket(source.Path);
        var profile = MakeProfile();
        var context = new RunContext(
            Guid.CreateVersion7(),
            packet,
            source.HeadSha,
            workspacePath,
            profile
        );

        var script = new ScriptedChatClient(
            MakeToolCallResponse(
                "call-1",
                "file_access_write",
                new Dictionary<string, object?>
                {
                    ["fileName"] = ".git/config",
                    ["content"] = "malicious",
                }
            ),
            MakeToolCallResponse(
                "call-2",
                "file_access_write",
                new Dictionary<string, object?>
                {
                    ["fileName"] = "greeting.txt",
                    ["content"] = "Hello from Tandem.",
                }
            ),
            MakeTextResponse("I created greeting.txt.")
        );

        var executor = new ImplementationBlockExecutor(_ => script);
        var binding = executor.BindExecutor();
        var workflow = new WorkflowBuilder(binding).WithOutputFrom(binding).Build();

        var sessionId = context.RunId.ToString("N");
        await using var runHandle = await InProcessExecution.RunStreamingAsync(
            workflow,
            context,
            sessionId,
            CancellationToken.None
        );

        BlockResult? result = null;
        Exception? failure = null;

        await foreach (var evt in runHandle.WatchStreamAsync(CancellationToken.None))
        {
            if (evt is WorkflowErrorEvent errorEvent)
            {
                failure = errorEvent.Exception;
            }
            else if (evt is ExecutorFailedEvent failedEvent)
            {
                failure = failedEvent.Data;
            }
            else if (evt is WorkflowOutputEvent output && output.Is<BlockResult>())
            {
                result = output.As<BlockResult>();
            }
        }

        failure
            .Should()
            .BeNull("the .git rejection should be a tool error, not a workflow failure");
        result.Should().NotBeNull();
        File.Exists(Path.Combine(workspacePath, ".git", "config"))
            .Should()
            .BeFalse(".git/config must not be written");
        File.Exists(Path.Combine(workspacePath, "greeting.txt"))
            .Should()
            .BeTrue("the normal write should succeed");
        File.ReadAllText(Path.Combine(workspacePath, "greeting.txt"))
            .Should()
            .Be("Hello from Tandem.");
    }

    private static Packet MakePacket(string repository) =>
        new(
            Title: "Add a greeting",
            Repository: repository,
            Base: "main",
            Outcomes:
            [
                new Outcome("greeting", "Create greeting.txt containing Hello from Tandem."),
            ],
            Verification: [],
            Constraints: ["Do not change existing files."],
            ImplementationContext: "Inspect the repository before making the requested change."
        );

    private static ResolvedProfile MakeProfile() =>
        new(
            ProviderName: "test",
            BaseUrl: "http://localhost:9999/v1",
            Model: "test-model",
            WireApi: WireApi.Completions,
            Reasoning: null,
            ContextWindowTokens: 200000,
            MaxOutputTokens: 32000,
            CheckpointAtPercent: 80
        );

    private static ChatResponse MakeToolCallResponse(
        string callId,
        string toolName,
        IDictionary<string, object?> arguments
    ) =>
        new(
            new ChatMessage(
                ChatRole.Assistant,
                [new FunctionCallContent(callId, toolName, arguments)]
            )
        )
        {
            FinishReason = ChatFinishReason.ToolCalls,
            ModelId = "test-model",
        };

    private static ChatResponse MakeTextResponse(string text) =>
        new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
        {
            FinishReason = ChatFinishReason.Stop,
            ModelId = "test-model",
        };

    private sealed class ScriptedChatClient(params ChatResponse[] responses) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(Dequeue());
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            var response = Dequeue();
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }

            await Task.CompletedTask;
        }

        private ChatResponse Dequeue()
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("ScriptedChatClient exhausted.");
            }

            return _responses.Dequeue();
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class TempSourceRepo : IDisposable
    {
        public string Path { get; }
        public string HeadSha { get; }

        private TempSourceRepo(string path, string headSha)
        {
            Path = path;
            HeadSha = headSha;
        }

        public static TempSourceRepo Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-src-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(path);
            RunGit(path, ["init", "-q"]);
            RunGit(path, ["config", "user.email", "t@t.test"]);
            RunGit(path, ["config", "user.name", "Tandem Test"]);
            File.WriteAllText(System.IO.Path.Combine(path, "anchor.txt"), "anchor\n");
            RunGit(path, ["add", "-A"]);
            RunGit(path, ["commit", "-qm", "init"]);
            RunGit(path, ["branch", "-m", "main"]);
            var shaResult = RunGit(path, ["rev-parse", "HEAD"]);
            return new TempSourceRepo(path, shaResult.Stdout.Trim());
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch { }
        }

        private static GitResult RunGit(string workingDir, string[] args)
        {
            using var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _gitPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDir,
                },
            };
            foreach (var a in args)
            {
                p.StartInfo.ArgumentList.Add(a);
            }

            p.Start();
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            return new GitResult(p.ExitCode, stdoutTask.Result, stderrTask.Result, false);
        }
    }

    private sealed class TempRunDir : IDisposable
    {
        public string Dir { get; }

        private TempRunDir(string dir) => Dir = dir;

        public static TempRunDir Create()
        {
            var dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-run-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(dir);
            return new TempRunDir(dir);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, recursive: true);
            }
            catch { }
        }
    }
}
