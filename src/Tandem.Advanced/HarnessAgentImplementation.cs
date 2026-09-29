using System.ComponentModel.DataAnnotations;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Agents.AI.Tools.Shell;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Advanced;

internal static class HarnessAgentImplementation
{
    internal static AIAgent Create(
        AgentImplementationContext context,
        string harnessInstructions,
        Func<string, bool, bool, (AIFunction? Search, AIFunction? Fetch)>? createWebTools = null,
        Func<string, string?>? getEnvironmentVariable = null
    )
    {
        var workspace = context.Workspace;
        var providers =
            context.Skills.Count == 0
                ? new List<AIContextProvider>()
                : [AgentSkillRuntime.CreateProvider(context.Skills)];
        if (workspace is not null)
        {
            var mafFileToolNames = HarnessTools.AddFileTools(
                context.ChatOptions,
                context.ToolEffects,
                workspace
            );
            if (mafFileToolNames.Count > 0)
            {
                providers.Add(
                    new FilteringAIContextProvider(
                        new FileAccessProvider(
                            new WorkspaceFileStore(workspace.Path),
                            new FileAccessProviderOptions
                            {
                                DisableWriteTools = !workspace.FileTools.Any(
                                    HarnessTools.IsMutation
                                ),
                                DisableReadOnlyToolApproval = true,
                                DisableWriteToolApproval = true,
                            }
                        ),
                        mafFileToolNames
                    )
                );
            }
            var existingToolCount = context.ChatOptions.Tools?.Count ?? 0;
            if (workspace.IncludeGitReadOnly)
            {
                ReadOnlyGitTools.Add(context.ChatOptions, workspace.Path, context.ToolEffects);
            }
            WorkspaceShellTools.Add(context.ChatOptions, workspace, context.ToolEffects);
            var workspaceTools = context.ChatOptions.Tools?.Skip(existingToolCount).ToArray() ?? [];
            if (workspaceTools.Length > 0)
            {
                context.ChatOptions.Tools = context
                    .ChatOptions.Tools?.Take(existingToolCount)
                    .ToList();
                providers.Add(new StaticToolsAIContextProvider(workspaceTools));
            }
            RegisteredWorkspaceTools.Add(context.ChatOptions, workspace, context.ToolEffects);
        }
        TavilyWebTools.Add(context, createWebTools, getEnvironmentVariable);
        return new HarnessAgent(
            context.ChatClient,
            CreateOptions(context, harnessInstructions, providers)
        );
    }

    internal static HarnessAgentOptions CreateOptions(
        AgentImplementationContext context,
        string harnessInstructions,
        IReadOnlyList<AIContextProvider>? providers = null
    ) =>
        new()
        {
            Id = context.Id,
            Name = context.Id,
            HarnessInstructions = harnessInstructions,
            ChatOptions = context.ChatOptions,
            DisableFileMemory = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableAgentSkillsProvider = true,
            AIContextProviders = providers is { Count: > 0 } ? providers : null,
            DisableWebSearch = true,
            DisableToolAutoApproval = true,
            DisableOpenTelemetry = true,
            MaxContextWindowTokens = context.MaxContextWindowTokens,
            MaxOutputTokens = context.MaxOutputTokens,
            DisableCompaction =
                context.DisableCompaction
                || context.MaxContextWindowTokens is null
                || context.MaxOutputTokens is null,
            CompactionStrategy =
                !context.DisableCompaction
                && context.MaxContextWindowTokens is { } ctx
                && context.MaxOutputTokens is { } output
                    ? BuildCompactionStrategy(ctx, output)
                    : null,
            MaximumIterationsPerRequest = 40,
            FileAccessStore = null,
        };

    private static CompactionStrategy BuildCompactionStrategy(
        int maxContextWindowTokens,
        int maxOutputTokens
    )
    {
        var inputBudget = maxContextWindowTokens - maxOutputTokens;
        return new PipelineCompactionStrategy(
            new ToolResultCompactionStrategy(
                CompactionTriggers.TokensExceed((int)(inputBudget * 0.5)),
                minimumPreservedGroups: 10
            )
            {
                ToolCallFormatter = _ => "[prior tool results compacted — consult the ledger]",
            },
            new TruncationCompactionStrategy(
                CompactionTriggers.TokensExceed((int)(inputBudget * 0.8)),
                minimumPreservedGroups: 10
            )
        );
    }
}

internal static class HarnessTools
{
    private sealed record FileTool(
        WorkspaceToolKind Kind,
        string Name,
        Infrastructure.ToolEffect Effect,
        Infrastructure.ToolEvidence Evidence,
        Func<string, AIFunction>? Create
    );

    // MAF's FileAccessProvider supplies the tools without a Create function.
    private static readonly FileTool[] _fileTools =
    [
        new(
            WorkspaceToolKind.ReadFile,
            FileAccessProvider.ReadFileToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection,
            WorkspaceFileReadTools.Create
        ),
        new(
            WorkspaceToolKind.ListFiles,
            FileAccessProvider.LsToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection,
            WorkspaceListTools.Create
        ),
        new(
            WorkspaceToolKind.Grep,
            FileAccessProvider.GrepToolName,
            Infrastructure.ToolEffect.Read,
            Infrastructure.ToolEvidence.RepositoryInspection,
            WorkspaceGrepTools.Create
        ),
        new(
            WorkspaceToolKind.WriteFile,
            FileAccessProvider.WriteToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.DeleteFile,
            FileAccessProvider.DeleteFileToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.Replace,
            FileAccessProvider.ReplaceToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.ReplaceLines,
            FileAccessProvider.ReplaceLinesToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            null
        ),
        new(
            WorkspaceToolKind.CopyFile,
            WorkspaceFileMutationTools.CopyToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            WorkspaceFileMutationTools.CreateCopyTool
        ),
        new(
            WorkspaceToolKind.MoveFile,
            WorkspaceFileMutationTools.MoveToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            WorkspaceFileMutationTools.CreateMoveTool
        ),
        new(
            WorkspaceToolKind.CreateDirectory,
            WorkspaceFileMutationTools.CreateDirectoryToolName,
            Infrastructure.ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEvidence.None,
            WorkspaceFileMutationTools.CreateDirectoryTool
        ),
    ];

    internal static bool IsMutation(WorkspaceToolKind kind) =>
        _fileTools.Single(tool => tool.Kind == kind).Effect
        == Infrastructure.ToolEffect.WorkspaceMutation;

    // Returns the selected tools that MAF's FileAccessProvider must expose.
    internal static IReadOnlySet<string> AddFileTools(
        ChatOptions options,
        ToolEffectRegistry effects,
        ResolvedAgentWorkspace workspace
    )
    {
        var mafToolNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in _fileTools.Where(tool => workspace.FileTools.Contains(tool.Kind)))
        {
            if (tool.Create is null)
            {
                effects.Add(tool.Name, tool.Effect, tool.Evidence);
                mafToolNames.Add(tool.Name);
            }
            else
            {
                Add(options, effects, tool.Create(workspace.Path), tool.Effect, tool.Evidence);
            }
        }
        return mafToolNames;
    }

    internal static void Add(
        ChatOptions options,
        ToolEffectRegistry effects,
        AITool tool,
        Infrastructure.ToolEffect effect,
        Infrastructure.ToolEvidence evidence = Infrastructure.ToolEvidence.None,
        Func<object?, ToolResultEvidenceDescriptor?>? resultEvidence = null
    )
    {
        var tools = options.Tools ?? [];
        if (tools.Any(existing => existing.Name == tool.Name))
        {
            throw new InvalidOperationException($"Agent already exposes tool '{tool.Name}'.");
        }
        options.Tools = [.. tools, tool];
        effects.Add(tool.Name, effect, evidence, resultEvidence);
    }
}

internal static class WorkspaceFileReadTools
{
    internal static AIFunction Create(string workspacePath) =>
        AIFunctionFactory.Create(
            async (
                [System.ComponentModel.Description("Repository-relative text file path.")]
                    string path,
                [System.ComponentModel.Description(
                    "One-based first line to read; use line numbers from grep."
                )]
                    int startLine = 1,
                [System.ComponentModel.Description("Maximum lines to return, from 1 to 2000.")]
                    int lineCount = 200,
                [System.ComponentModel.Description(
                    "Zero-based character offset within startLine; continues a truncated line using the returned nextCharacterOffset."
                )]
                    int characterOffset = 0,
                CancellationToken cancellationToken = default
            ) =>
                await BoundedLinePageReader.ReadAsync(
                    WorkspacePathAuthority.Resolve(workspacePath, path, "read"),
                    startLine,
                    lineCount,
                    characterOffset,
                    cancellationToken
                ),
            FileAccessProvider.ReadFileToolName,
            "Read source lines using startLine from grep. Continue with the returned nextStartLine and nextCharacterOffset, including the remainder of oversized lines. Restart after edits."
        );
}

internal static class WorkspacePathAuthority
{
    internal static string Resolve(string workspacePath, string path, string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            throw new WorkspacePathException("File paths must be relative to the workspace.");
        }
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspacePath));
        var fullPath = Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, fullPath);
        if (
            relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal)
        )
        {
            throw new WorkspacePathException("File paths must remain within the workspace.");
        }
        if (
            relative
                .Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries
                )
                .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase))
        )
        {
            throw new WorkspacePathException("Access to Git metadata is not allowed.");
        }
        var current = root;
        foreach (
            var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries
            )
        )
        {
            current = Path.Combine(current, segment);
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new WorkspacePathException(
                        $"Workspace {operation} paths cannot contain symbolic links or reparse points."
                    );
                }
            }
            catch (FileNotFoundException)
            {
                break;
            }
            catch (DirectoryNotFoundException)
            {
                break;
            }
        }
        return fullPath;
    }
}

internal static class WorkspaceFileMutationTools
{
    internal const string CopyToolName = "file_access_copy";
    internal const string MoveToolName = "file_access_move";
    internal const string CreateDirectoryToolName = "file_access_create_directory";

    internal static AIFunction CreateCopyTool(string workspacePath) =>
        AIFunctionFactory.Create(
            (
                string sourceFileName,
                string destinationFileName,
                bool overwrite,
                CancellationToken cancellationToken
            ) =>
                Copy(
                    workspacePath,
                    sourceFileName,
                    destinationFileName,
                    overwrite,
                    cancellationToken
                ),
            CopyToolName,
            "Copy an existing file byte-for-byte within the configured workspace."
        );

    internal static AIFunction CreateMoveTool(string workspacePath) =>
        AIFunctionFactory.Create(
            (
                string sourceFileName,
                string destinationFileName,
                bool overwrite,
                CancellationToken cancellationToken
            ) =>
                Move(
                    workspacePath,
                    sourceFileName,
                    destinationFileName,
                    overwrite,
                    cancellationToken
                ),
            MoveToolName,
            "Move an existing file byte-for-byte within the configured workspace."
        );

    internal static AIFunction CreateDirectoryTool(string workspacePath) =>
        AIFunctionFactory.Create(
            (string directoryName, CancellationToken cancellationToken) =>
                CreateDirectory(workspacePath, directoryName, cancellationToken),
            CreateDirectoryToolName,
            "Create a directory and any missing parent directories within the configured workspace."
        );

    internal static string Copy(
        string workspacePath,
        string sourceFileName,
        string destinationFileName,
        bool overwrite,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(workspacePath, sourceFileName);
        var destination = Resolve(workspacePath, destinationFileName);
        Transfer(() => File.Copy(source, destination, overwrite), destination, overwrite);
        return $"Copied '{sourceFileName}' to '{destinationFileName}'.";
    }

    internal static string Move(
        string workspacePath,
        string sourceFileName,
        string destinationFileName,
        bool overwrite,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = Resolve(workspacePath, sourceFileName);
        var destination = Resolve(workspacePath, destinationFileName);
        Transfer(() => File.Move(source, destination, overwrite), destination, overwrite);
        return $"Moved '{sourceFileName}' to '{destinationFileName}'.";
    }

    private static void Transfer(Action transfer, string destination, bool overwrite)
    {
        try
        {
            transfer();
        }
        catch (IOException exception)
            when (!overwrite && (File.Exists(destination) || Directory.Exists(destination)))
        {
            throw new ToolInputException(
                $"Destination already exists: {Path.GetFileName(destination)}. Nothing was overwritten. Choose another destination or explicitly allow overwrite. {exception.Message}"
            );
        }
    }

    internal static string CreateDirectory(
        string workspacePath,
        string directoryName,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Resolve(workspacePath, directoryName));
        return $"Created directory '{directoryName}'.";
    }

    private static string Resolve(string workspacePath, string fileName) =>
        WorkspacePathAuthority.Resolve(workspacePath, fileName, "mutation");
}

internal sealed class FilteringAIContextProvider(
    AIContextProvider inner,
    IReadOnlySet<string> selectedToolNames
) : AIContextProvider
{
    protected override async ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken
    )
    {
        var existingTools = context.AIContext.Tools?.ToArray() ?? [];
        var result = await inner.InvokingAsync(context, cancellationToken);
        result.Tools =
        [
            .. existingTools,
            .. (result.Tools ?? []).Where(tool =>
                selectedToolNames.Contains(tool.Name)
                && existingTools.All(existing => existing.Name != tool.Name)
            ),
        ];
        return result;
    }

    protected override ValueTask InvokedCoreAsync(
        InvokedContext context,
        CancellationToken cancellationToken
    ) => inner.InvokedAsync(context, cancellationToken);
}

internal static class RegisteredWorkspaceTools
{
    internal static void Add(
        ChatOptions options,
        ResolvedAgentWorkspace workspace,
        ToolEffectRegistry effects
    )
    {
        foreach (var registration in workspace.RegisteredTools ?? [])
        {
            var tool = registration.Create(workspace.Path);
            if (!string.Equals(tool.Name, registration.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Registered workspace tool '{registration.Name}' created tool '{tool.Name}'."
                );
            }
            HarnessTools.Add(options, effects, tool, registration.Effect, registration.Evidence);
        }
    }
}

internal sealed class StaticToolsAIContextProvider(IReadOnlyList<AITool> tools) : AIContextProvider
{
    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken
    )
    {
        context.AIContext.Tools = [.. context.AIContext.Tools ?? [], .. tools];
        return ValueTask.FromResult(context.AIContext);
    }
}

internal static class WorkspaceShellTools
{
    internal static void Add(
        ChatOptions options,
        ResolvedAgentWorkspace workspace,
        ToolEffectRegistry effects,
        TimeSpan? timeout = null,
        int maxOutputBytes = 16 * 1024 * 1024
    )
    {
        foreach (var command in workspace.Commands)
        {
            HarnessTools.Add(
                options,
                effects,
                CreateCommandFunction(command, workspace.Path, timeout, maxOutputBytes),
                Infrastructure.ToolEffect.ProcessExecution,
                resultEvidence: ToProcessEvidence
            );
        }
        if (workspace.IncludeShell)
        {
            HarnessTools.Add(
                options,
                effects,
                CreateExecutor(workspace.Path, acknowledgeUnsafe: true, timeout, maxOutputBytes)
                    .AsAIFunction(
                        "run_shell",
                        "Run a model-authored command in the configured workspace without approval.",
                        requireApproval: false
                    ),
                Infrastructure.ToolEffect.ProcessExecution
            );
        }
    }

    private static readonly JsonSerializerOptions _commandJson = new(AIJsonUtilities.DefaultOptions)
    {
        RespectNullableAnnotations = true,
    };

    private static AIFunction CreateCommandFunction(
        AgentCommandDescriptor command,
        string workspacePath,
        TimeSpan? timeout,
        int maxOutputBytes
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
                    RunCommandAsync(
                        command,
                        [],
                        workspacePath,
                        timeout,
                        maxOutputBytes,
                        cancellationToken
                    ),
                options
            )
            : new CommandArgumentsFunction(
                AIFunctionFactory.Create(
                    (
                        CancellationToken cancellationToken,
                        [Length(0, AgentCommand.MaximumArgumentCount)] string[] arguments = null!
                    ) =>
                        RunCommandAsync(
                            command,
                            arguments ?? [],
                            workspacePath,
                            timeout,
                            maxOutputBytes,
                            cancellationToken
                        ),
                    options
                )
            );
    }

    private static async Task<ShellResult> RunCommandAsync(
        AgentCommandDescriptor command,
        string[] arguments,
        string workspacePath,
        TimeSpan? timeout,
        int maxOutputBytes,
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
        await using var executor = CreateExecutor(
            workspacePath,
            acknowledgeUnsafe: false,
            timeout,
            maxOutputBytes
        );
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

    private static ToolResultEvidenceDescriptor.Process? ToProcessEvidence(object? result) =>
        result is JsonElement { ValueKind: JsonValueKind.Object } element
        && element.Deserialize<ShellResult>(AIJsonUtilities.DefaultOptions) is { } shell
            ? new ToolResultEvidenceDescriptor.Process(
                shell.ExitCode,
                shell.Stdout,
                shell.Stderr,
                shell.Duration,
                shell.TimedOut,
                shell.Truncated
            )
            : null;

    internal static LocalShellExecutor CreateExecutor(
        string workspacePath,
        bool acknowledgeUnsafe,
        TimeSpan? timeout,
        int maxOutputBytes
    ) =>
        new(
            new LocalShellExecutorOptions
            {
                Mode = ShellMode.Stateless,
                Shell = OperatingSystem.IsWindows() ? "powershell.exe" : null,
                WorkingDirectory = workspacePath,
                ConfineWorkingDirectory = true,
                Timeout = timeout ?? TimeSpan.FromMinutes(10),
                MaxOutputBytes = maxOutputBytes,
                AcknowledgeUnsafe = acknowledgeUnsafe,
            }
        );
}

// MAF's file tools reach only write, read, exists and delete: Tandem replaces its grep, ls and read tools.
internal sealed class WorkspaceFileStore(string workspacePath) : AgentFileStore
{
    private static readonly UTF8Encoding _utf8WithoutBom = new(false);

    public override async Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken
    )
    {
        var fullPath = WorkspacePathAuthority.Resolve(workspacePath, path, "write");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            content.StartsWith('\uFEFF') ? content[1..] : content,
            _utf8WithoutBom,
            cancellationToken
        );
    }

    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = WorkspacePathAuthority.Resolve(workspacePath, path, "read");
        return File.Exists(fullPath)
            ? await File.ReadAllTextAsync(fullPath, cancellationToken)
            : null;
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        var fullPath = WorkspacePathAuthority.Resolve(workspacePath, path, "mutation");
        if (!File.Exists(fullPath))
        {
            return Task.FromResult(false);
        }
        File.Delete(fullPath);
        return Task.FromResult(true);
    }

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        Task.FromResult(File.Exists(WorkspacePathAuthority.Resolve(workspacePath, path, "read")));

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException("Tandem's workspace ls tool replaces MAF's.");

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken
    ) => throw new NotSupportedException("Tandem's workspace grep tool replaces MAF's.");

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        throw new NotSupportedException("MAF's file tools do not create directories.");
}

#pragma warning restore MAAI001
