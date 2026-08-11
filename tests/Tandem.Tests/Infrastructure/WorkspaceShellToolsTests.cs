using System.Text.Json;
using FluentAssertions;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;
using RuntimeToolEffect = Tandem.Infrastructure.ToolEffect;

namespace Tandem.Tests.Infrastructure;

public sealed class WorkspaceShellToolsTests
{
    [Fact]
    public async Task FixedCommand_IsParameterlessAndRunsInWorkspace()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new ChatOptions();
        var effects = new ToolEffectRegistry();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(
                workspace.Path,
                [
                    new AgentCommandDescriptor(
                        "where_am_i",
                        "Print the workspace.",
                        CurrentDirectory()
                    ),
                ]
            ),
            effects
        );
        var tool = options
            .Tools!.Should()
            .ContainSingle()
            .Which.Should()
            .BeAssignableTo<AIFunction>()
            .Subject;

        var result = Result(await tool.InvokeAsync(new AIFunctionArguments()));

        result.ExitCode.Should().Be(0);
        result.Stdout.Trim().Should().EndWith(System.IO.Path.GetFileName(workspace.Path));
        tool.JsonSchema.GetProperty("properties").EnumerateObject().Should().BeEmpty();
        effects.TryGet("where_am_i", out var semantics).Should().BeTrue();
        semantics.Effect.Should().Be(RuntimeToolEffect.ProcessExecution);
    }

    [Fact]
    public async Task FixedCommand_ReturnsStderrAndExitCodeToTheCaller()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new ChatOptions();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(
                workspace.Path,
                [new AgentCommandDescriptor("fail", "Fail with evidence.", FailureCommand())]
            ),
            new ToolEffectRegistry()
        );

        var result = Result(
            await ((AIFunction)options.Tools!.Single()).InvokeAsync(new AIFunctionArguments())
        );

        result.ExitCode.Should().Be(7);
        result.Stderr.Should().Contain("expected failure");
    }

    [Fact]
    public async Task UnrestrictedShell_IsExplicitAndStateless()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new ChatOptions();
        var effects = new ToolEffectRegistry();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(workspace.Path, [], includeShell: true),
            effects
        );
        var tool = options
            .Tools!.Should()
            .ContainSingle()
            .Which.Should()
            .BeAssignableTo<AIFunction>()
            .Subject;

        await tool.InvokeAsync(new AIFunctionArguments { ["command"] = SetVariableCommand() });
        var second = Text(
            await tool.InvokeAsync(new AIFunctionArguments { ["command"] = ReadVariableCommand() })
        );

        tool.Name.Should().Be("run_shell");
        second.Should().NotContain("retained-value");
        effects.TryGet("run_shell", out var semantics).Should().BeTrue();
        semantics.Effect.Should().Be(RuntimeToolEffect.ProcessExecution);
        tool.JsonSchema.GetProperty("properties")
            .TryGetProperty("command", out _)
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task FixedCommand_StopsAtConfiguredTimeout()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new ChatOptions();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(
                workspace.Path,
                [new AgentCommandDescriptor("slow", "Run slowly.", SlowCommand())]
            ),
            new ToolEffectRegistry(),
            TimeSpan.FromMilliseconds(100)
        );

        var result = Result(
            await ((AIFunction)options.Tools!.Single()).InvokeAsync(new AIFunctionArguments())
        );

        result.TimedOut.Should().BeTrue();
        result.ExitCode.Should().Be(124);
    }

    [Fact]
    public async Task FixedCommand_HonorsCallerCancellation()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new ChatOptions();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(
                workspace.Path,
                [new AgentCommandDescriptor("slow", "Run slowly.", SlowCommand())]
            ),
            new ToolEffectRegistry()
        );
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var invoke = async () =>
            await ((AIFunction)options.Tools!.Single()).InvokeAsync(
                new AIFunctionArguments(),
                cancellation.Token
            );

        await invoke.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task FixedCommand_TruncatesBoundedOutput()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new ChatOptions();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(
                workspace.Path,
                [new AgentCommandDescriptor("noisy", "Produce output.", NoisyCommand())]
            ),
            new ToolEffectRegistry(),
            maxOutputBytes: 256
        );

        var result = Result(
            await ((AIFunction)options.Tools!.Single()).InvokeAsync(new AIFunctionArguments())
        );

        result.Truncated.Should().BeTrue();
        result.Stdout.Length.Should().BeLessThan(1_000);
    }

    private static ResolvedAgentWorkspace ResolvedWorkspace(
        string path,
        IReadOnlyList<AgentCommandDescriptor> commands,
        bool includeShell = false
    ) => new(path, new HashSet<WorkspaceToolKind>(), false, includeShell, commands);

    private static ShellResult Result(object? value) =>
        ((JsonElement)value!).Deserialize<ShellResult>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
        )!;

    private static string Text(object? value) =>
        value is JsonElement { ValueKind: JsonValueKind.String } element
            ? element.GetString()!
            : value?.ToString() ?? "";

    private static string CurrentDirectory() => OperatingSystem.IsWindows() ? "cd" : "pwd";

    private static string FailureCommand() =>
        OperatingSystem.IsWindows()
            ? "echo expected failure 1>&2 & exit /b 7"
            : "printf 'expected failure\\n' >&2; exit 7";

    private static string SetVariableCommand() =>
        OperatingSystem.IsWindows()
            ? "set TANDEM_SHELL_STATE=retained-value"
            : "export TANDEM_SHELL_STATE=retained-value";

    private static string ReadVariableCommand() =>
        OperatingSystem.IsWindows()
            ? "echo %TANDEM_SHELL_STATE%"
            : "printf '%s' \"$TANDEM_SHELL_STATE\"";

    private static string SlowCommand() =>
        OperatingSystem.IsWindows() ? "ping -n 6 127.0.0.1 >nul" : "sleep 5";

    private static string NoisyCommand() =>
        OperatingSystem.IsWindows()
            ? "powershell -NoProfile -Command \"[Console]::Out.Write('x' * 10000)\""
            : "printf '%010000d' 0";

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string path) => Path = path;

        internal string Path { get; }

        internal static TemporaryWorkspace Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"tandem-shell-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(path);
            return new TemporaryWorkspace(path);
        }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
