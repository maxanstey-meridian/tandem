using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

namespace Tandem.Advanced;

internal static class WorkspaceShellTools
{
    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(10);
    private const int MaximumOutputBytes = 16 * 1024 * 1024;

    internal static void Add(
        ChatOptions options,
        ResolvedAgentWorkspace workspace,
        ToolEffectRegistry effects
    )
    {
        foreach (var command in workspace.Commands)
        {
            HarnessTools.Add(
                options,
                effects,
                CreateCommandFunction(command, workspace.Path),
                ToolEffect.ProcessExecution,
                resultEvidence: ToProcessEvidence
            );
        }
        if (workspace.IncludeShell)
        {
            HarnessTools.AddBuiltIn(
                options,
                effects,
                CreateExecutor(workspace.Path)
                    .AsAIFunction(
                        "run_shell",
                        "Run a model-authored command in the configured workspace without approval.",
                        requireApproval: false
                    )
            );
        }
    }

    private static readonly JsonSerializerOptions _commandJson = new(AIJsonUtilities.DefaultOptions)
    {
        RespectNullableAnnotations = true,
    };

    private static AIFunction CreateCommandFunction(
        AgentCommandDescriptor command,
        string workspacePath
    )
    {
        var options = new AIFunctionFactoryOptions
        {
            Name = command.Name,
            Description = command.Description,
            SerializerOptions = _commandJson,
            JsonSchemaCreateOptions = new AIJsonSchemaCreateOptions
            {
                TransformOptions = new AIJsonSchemaTransformOptions
                {
                    DisallowAdditionalProperties = true,
                },
                TransformSchemaNode = (context, node) =>
                {
                    if (context.Path.IsEmpty && context.TypeInfo.Type == typeof(string[]))
                    {
                        node["description"] = command.Description;
                        node["examples"] = new JsonArray(
                            new JsonArray([.. command.Arguments.Select(a => JsonValue.Create(a))])
                        );
                        node["items"]!["maxLength"] = AgentCommand.MaximumArgumentLength;
                        node.AsObject().Remove("default");
                    }
                    return node;
                },
            },
        };
        return command.Arguments.Count == 0
            ? AIFunctionFactory.Create(
                (CancellationToken cancellationToken) =>
                    RunCommandAsync(command, [], workspacePath, cancellationToken),
                options
            )
            : new CommandArgumentsFunction(
                AIFunctionFactory.Create(
                    (
                        CancellationToken cancellationToken,
                        [Length(0, AgentCommand.MaximumArgumentCount)] string[] arguments = null!
                    ) =>
                        RunCommandAsync(command, arguments ?? [], workspacePath, cancellationToken),
                    options
                )
            );
    }

    private static async Task<ShellResult> RunCommandAsync(
        AgentCommandDescriptor command,
        string[] arguments,
        string workspacePath,
        CancellationToken cancellationToken
    )
    {
        if (arguments.Length > AgentCommand.MaximumArgumentCount)
        {
            throw new ToolInputException(
                $"Command '{command.Name}' accepts at most {AgentCommand.MaximumArgumentCount} arguments."
            );
        }
        if (
            arguments.Any(argument =>
                argument is null || argument.Length > AgentCommand.MaximumArgumentLength
            )
        )
        {
            throw new ToolInputException(
                $"Arguments of command '{command.Name}' must be strings of at most {AgentCommand.MaximumArgumentLength} characters."
            );
        }
        Func<string, string> quote = OperatingSystem.IsWindows() ? QuotePowerShell : QuotePosix;
        var text = string.Join(' ', [command.Command, .. arguments.Select(quote)]);
        await using var executor = CreateExecutor(workspacePath);
        return await executor.RunAsync(text, cancellationToken);
    }

    // MAF uses these same dialect-specific forms internally, but does not expose them publicly.
    private static string QuotePowerShell(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string QuotePosix(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    // Process tools only report ToolInputException back to the model, so binding failures are translated.
    private sealed class CommandArgumentsFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            if (
                arguments.TryGetValue("arguments", out var value)
                && value is null or JsonElement { ValueKind: JsonValueKind.Null }
            )
            {
                throw new ToolInputException(
                    $"Argument 'arguments' of command '{Name}' cannot be null."
                );
            }
            try
            {
                return await base.InvokeCoreAsync(arguments, cancellationToken);
            }
            catch (JsonException exception)
            {
                throw new ToolInputException(
                    $"Argument 'arguments' of command '{Name}' must be an array of strings. {exception.Message}"
                );
            }
        }
    }

    private static ToolResultEvidence.Process? ToProcessEvidence(object? result) =>
        result is JsonElement { ValueKind: JsonValueKind.Object } element
        && element.Deserialize<ShellResult>(AIJsonUtilities.DefaultOptions) is { } shell
            ? new ToolResultEvidence.Process(
                shell.ExitCode,
                shell.Stdout,
                shell.Stderr,
                shell.Duration,
                shell.TimedOut,
                shell.Truncated
            )
            : null;

    // Commands call RunAsync directly; run_shell is exposed without approval by explicit workspace choice.
    internal static LocalShellExecutor CreateExecutor(string workspacePath) =>
        new(
            new LocalShellExecutorOptions
            {
                Mode = ShellMode.Stateless,
                Shell = OperatingSystem.IsWindows() ? "powershell.exe" : null,
                WorkingDirectory = workspacePath,
                ConfineWorkingDirectory = true,
                Timeout = _timeout,
                MaxOutputBytes = MaximumOutputBytes,
                AcknowledgeUnsafe = true,
            }
        );
}
