using System.ComponentModel;
using FluentAssertions;

namespace Tandem.Tests.Infrastructure;

public sealed class LocalProcessTests
{
    [Fact]
    public async Task Arguments_working_directory_and_environment_are_literal()
    {
        using var directory = TemporaryDirectory.Create();
        var variable = $"TANDEM_PROCESS_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(variable, "inherited");
        try
        {
            var result = await RunChildAsync(
                ["inspect", variable, "space value", "$HOME", "*.txt", "", "; exit 9"],
                workingDirectory: directory.Path,
                environment: new Dictionary<string, string> { [variable] = "overlay" }
            );

            result.ExitCode.Should().Be(0);
            result.Stdout.Split('\n')[0].Should().EndWith(Path.GetFileName(directory.Path));
            result.Stdout.Should().Contain("overlay");
            result.Stdout.Should().Contain("[space value]").And.Contain("[$HOME]");
            result.Stdout.Should().Contain("[*.txt]").And.Contain("[]").And.Contain("[; exit 9]");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Fact]
    public async Task Returns_stdout_stderr_nonzero_exit_and_duration()
    {
        var result = await RunChildAsync(["result", "out-✓", "err-✓", "17"]);

        result.ExitCode.Should().Be(17);
        result.Stdout.Should().Be("out-✓");
        result.Stderr.Should().Be("err-✓");
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task Captures_each_stream_by_utf8_bytes_and_drains_noisy_output()
    {
        var result = await RunChildAsync(["noisy", "1000000"], maximumBytes: 1025);

        result.ExitCode.Should().Be(0);
        result.Stdout.Length.Should().Be(1025);
        result.Stderr.Length.Should().Be(1025);
        result.StdoutTruncated.Should().BeTrue();
        result.StderrTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_utf8_uses_replacement_fallback_at_the_byte_boundary()
    {
        var result = await RunChildAsync(["result", "✓", "", "0"], maximumBytes: 2);

        result.Stdout.Should().Be("�");
        result.StdoutTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task Timeout_kills_the_process_tree_and_returns_deterministic_result()
    {
        using var directory = TemporaryDirectory.Create();
        var marker = Path.Combine(directory.Path, "descendant-alive");

        var result = await RunChildAsync(["tree", marker], timeout: TimeSpan.FromMilliseconds(500));

        result.ExitCode.Should().Be(-1);
        result.TimedOut.Should().BeTrue();
        await Task.Delay(TimeSpan.FromSeconds(3));
        File.Exists(marker).Should().BeFalse();
    }

    [Fact]
    public async Task Caller_cancellation_kills_then_throws()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var run = async () => await RunChildAsync(["wait"], cancellationToken: cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Pre_cancelled_token_does_not_start_the_process()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var run = async () =>
            await LocalProcess.RunAsync(
                new LocalProcessRequest(MissingExecutable(), []),
                cancellation.Token
            );

        await run.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Missing_executable_remains_a_start_exception()
    {
        var run = async () =>
            await LocalProcess.RunAsync(new LocalProcessRequest(MissingExecutable(), []));

        await run.Should().ThrowAsync<Win32Exception>();
    }

    [Fact]
    public async Task Missing_working_directory_is_rejected_before_start()
    {
        var run = async () =>
            await LocalProcess.RunAsync(
                new LocalProcessRequest(
                    "unused",
                    [],
                    Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))
                )
            );

        await run.Should().ThrowAsync<DirectoryNotFoundException>();
    }

    [Fact]
    public async Task Request_validation_rejects_invalid_values()
    {
        var nullArgument = async () =>
            await LocalProcess.RunAsync(new LocalProcessRequest("dotnet", [null!]));
        var blankFile = async () => await LocalProcess.RunAsync(new LocalProcessRequest(" ", []));
        var timeout = async () =>
            await LocalProcess.RunAsync(
                new LocalProcessRequest("dotnet", [], Timeout: TimeSpan.Zero)
            );
        var bound = async () =>
            await LocalProcess.RunAsync(
                new LocalProcessRequest("dotnet", [], MaximumOutputBytesPerStream: 0)
            );
        var nullEnvironmentName = async () =>
            await LocalProcess.RunAsync(
                new LocalProcessRequest("dotnet", [], Environment: new NullKeyEnvironment())
            );
        var blankEnvironmentName = async () =>
            await LocalProcess.RunAsync(
                new LocalProcessRequest(
                    "dotnet",
                    [],
                    Environment: new Dictionary<string, string> { [" "] = "value" }
                )
            );
        var nullEnvironmentValue = async () =>
            await LocalProcess.RunAsync(
                new LocalProcessRequest(
                    "dotnet",
                    [],
                    Environment: new Dictionary<string, string> { ["NAME"] = null! }
                )
            );
        var excessiveBound = async () =>
            await LocalProcess.RunAsync(
                new LocalProcessRequest(
                    "dotnet",
                    [],
                    MaximumOutputBytesPerStream: 16 * 1024 * 1024 + 1
                )
            );

        await nullArgument.Should().ThrowAsync<ArgumentException>();
        await blankFile.Should().ThrowAsync<ArgumentException>();
        await timeout.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await bound.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await nullEnvironmentName.Should().ThrowAsync<ArgumentException>();
        await blankEnvironmentName.Should().ThrowAsync<ArgumentException>();
        await nullEnvironmentValue.Should().ThrowAsync<ArgumentException>();
        await excessiveBound.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static Task<LocalProcessResult> RunChildAsync(
        IReadOnlyList<string> arguments,
        string? workingDirectory = null,
        TimeSpan? timeout = null,
        int maximumBytes = 64 * 1024,
        IReadOnlyDictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default
    ) =>
        LocalProcess.RunAsync(
            new LocalProcessRequest(
                "dotnet",
                [ChildAssemblyPath(), .. arguments],
                workingDirectory,
                timeout,
                maximumBytes,
                environment
            ),
            cancellationToken
        );

    private static string ChildAssemblyPath() =>
        Path.GetFullPath(
            "../../../../Tandem.Process.TestChild/bin/Debug/net10.0/Tandem.Process.TestChild.dll",
            AppContext.BaseDirectory
        );

    private static string MissingExecutable() => $"tandem-missing-{Guid.NewGuid():N}";

    private sealed class TemporaryDirectory : IDisposable
    {
        private TemporaryDirectory(string path) => Path = path;

        internal string Path { get; }

        internal static TemporaryDirectory Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tandem-process-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(path);
            return new TemporaryDirectory(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class NullKeyEnvironment : IReadOnlyDictionary<string, string>
    {
        public int Count => 1;
        public IEnumerable<string> Keys => [null!];
        public IEnumerable<string> Values => ["value"];
        public string this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            yield return new KeyValuePair<string, string>(null!, "value");
        }

        public bool TryGetValue(string key, out string value)
        {
            value = null!;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
