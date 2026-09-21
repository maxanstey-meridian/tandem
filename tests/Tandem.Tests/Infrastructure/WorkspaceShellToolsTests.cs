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
                        CurrentDirectory(),
                        []
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
                [new AgentCommandDescriptor("fail", "Fail with evidence.", FailureCommand(), [])]
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
    public async Task ParameterizedCommand_EmitsArraySchemaValidatesAndQuotesArguments()
    {
        using var workspace = TemporaryWorkspace.Create();
        string command;
        if (OperatingSystem.IsWindows())
        {
            command = "Set-Content -NoNewline -Path received.txt -Value";
        }
        else
        {
            var script = System.IO.Path.Combine(workspace.Path, "capture.sh");
            await File.WriteAllTextAsync(script, "#!/bin/sh\nprintf '%s' \"$@\" > received.txt\n");
            File.SetUnixFileMode(
                script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
            command = "./capture.sh";
        }
        var options = new ChatOptions();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(
                workspace.Path,
                [new AgentCommandDescriptor("capture", "Capture one value.", command, ["--value"])]
            ),
            new ToolEffectRegistry()
        );
        var tool = (AIFunction)options.Tools!.Single();
        var property = tool.JsonSchema.GetProperty("properties").GetProperty("arguments");
        property.GetProperty("type").GetString().Should().Be("array");
        property.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
        property.GetProperty("description").GetString().Should().Be("Capture one value.");
        tool.JsonSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        const string value =
            "spaces ' \" $() `touch marker` ; New-Item marker ; && || | > <\n* $HOME";
        await tool.InvokeAsync(
            new AIFunctionArguments
            {
                ["arguments"] = JsonSerializer.SerializeToElement(new[] { value }),
            }
        );

        (await File.ReadAllTextAsync(System.IO.Path.Combine(workspace.Path, "received.txt")))
            .Should()
            .Be(value);
        File.Exists(System.IO.Path.Combine(workspace.Path, "marker")).Should().BeFalse();
        var nonArray = async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["arguments"] = "x" });
        await nonArray.Should().ThrowAsync<ArgumentException>();
        var nonStringElement = async () =>
            await tool.InvokeAsync(
                new AIFunctionArguments
                {
                    ["arguments"] = JsonSerializer.SerializeToElement(new object?[] { 42 }),
                }
            );
        await nonStringElement.Should().ThrowAsync<ArgumentException>();
        var explicitNull = async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["arguments"] = null });
        await explicitNull.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ParameterizedCommand_EnforcesCountAndLengthBoundsAtInvocation()
    {
        using var workspace = TemporaryWorkspace.Create();
        var options = new ChatOptions();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(
                workspace.Path,
                [new AgentCommandDescriptor("capture", "Capture.", "echo", ["--value"])]
            ),
            new ToolEffectRegistry()
        );
        var tool = (AIFunction)options.Tools!.Single();

        var tooMany = async () =>
            await tool.InvokeAsync(
                new AIFunctionArguments
                {
                    ["arguments"] = JsonSerializer.SerializeToElement(
                        Enumerable
                            .Range(0, AgentCommand.MaximumArgumentCount + 1)
                            .Select(index => $"arg{index}")
                            .ToArray()
                    ),
                }
            );
        await tooMany.Should().ThrowAsync<ArgumentException>();

        var tooLong = async () =>
            await tool.InvokeAsync(
                new AIFunctionArguments
                {
                    ["arguments"] = JsonSerializer.SerializeToElement(
                        new[] { new string('x', AgentCommand.MaximumArgumentLength + 1) }
                    ),
                }
            );
        await tooLong.Should().ThrowAsync<ArgumentException>();

        var unknownProperty = async () =>
            await tool.InvokeAsync(new AIFunctionArguments { ["other"] = "x" });
        await unknownProperty.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WindowsExecutor_IsPinnedToPowerShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = TemporaryWorkspace.Create();
        await using var executor = WorkspaceShellTools.CreateExecutor(
            workspace.Path,
            acknowledgeUnsafe: false,
            timeout: null,
            maxOutputBytes: 1024
        );

        System.IO.Path.GetFileName(executor.ResolvedShellBinary).Should().Be("powershell.exe");
    }

    [Fact]
    public void PublicCommandArguments_EnforceCountAndLengthBoundsAtDefineTime()
    {
        var command = AgentCommand.Define(
            "run_review",
            "Run review.",
            "review",
            new[] { "src/review.cs", "--thorough" }
        );
        command.Arguments.Should().Equal("src/review.cs", "--thorough");

        FluentActions
            .Invoking(() =>
                AgentCommand.Define(
                    "invalid",
                    "Invalid.",
                    "invalid",
                    Enumerable
                        .Range(0, AgentCommand.MaximumArgumentCount + 1)
                        .Select(index => $"arg{index}")
                        .ToArray()
                )
            )
            .Should()
            .Throw<ArgumentOutOfRangeException>();

        FluentActions
            .Invoking(() =>
                AgentCommand.Define(
                    "invalid",
                    "Invalid.",
                    "invalid",
                    new[] { new string('x', AgentCommand.MaximumArgumentLength + 1) }
                )
            )
            .Should()
            .Throw<ArgumentOutOfRangeException>();

        FluentActions
            .Invoking(() => AgentCommand.Define("invalid", "Invalid.", "invalid", [null!]))
            .Should()
            .Throw<ArgumentNullException>();
    }

    [Fact]
    public void PacketCommandAdmission_CarriesOptionalArgumentsThroughToTheToolSchema()
    {
        using var workspace = TemporaryWorkspace.Create();
        var argumented = AgentCommand.Define(
            "run_review",
            "Run review with a path.",
            "review",
            ["--path"]
        );
        var labelOnly = AgentCommand.Define(
            "where_am_i",
            "Print the workspace.",
            CurrentDirectory()
        );

        var options = new ChatOptions();
        WorkspaceShellTools.Add(
            options,
            ResolvedWorkspace(
                workspace.Path,
                [argumented.ToDescriptor(), labelOnly.ToDescriptor()]
            ),
            new ToolEffectRegistry()
        );

        var tools = options.Tools!.Cast<AIFunction>().ToArray();
        var reviewTool = tools.Should().ContainSingle(tool => tool.Name == "run_review").Subject;
        var property = reviewTool.JsonSchema.GetProperty("properties").GetProperty("arguments");
        property.GetProperty("type").GetString().Should().Be("array");
        property.GetProperty("items").GetProperty("type").GetString().Should().Be("string");
        property.GetProperty("examples")[0][0].GetString().Should().Be("--path");
        property.GetProperty("maxItems").GetInt32().Should().Be(16);
        reviewTool.JsonSchema.GetProperty("additionalProperties").GetBoolean().Should().BeFalse();

        var labelOnlyTool = tools.Should().ContainSingle(tool => tool.Name == "where_am_i").Subject;
        labelOnlyTool.JsonSchema.GetProperty("properties").EnumerateObject().Should().BeEmpty();
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
                [new AgentCommandDescriptor("slow", "Run slowly.", SlowCommand(), [])]
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
                [new AgentCommandDescriptor("slow", "Run slowly.", SlowCommand(), [])]
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
                [new AgentCommandDescriptor("noisy", "Produce output.", NoisyCommand(), [])]
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
    ) => new(path, new HashSet<WorkspaceToolKind>(), false, includeShell, false, false, commands);

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
