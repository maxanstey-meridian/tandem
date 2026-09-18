using System.Text;
using System.Text.Json;
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
        AgentFileStore? fileStore = workspace is null
            ? null
            : new GitExcludedFileStore(new BomlessFileSystemAgentFileStore(workspace.Path));
        var providers =
            context.Skills.Count == 0
                ? new List<AIContextProvider>()
                : [AgentSkillRuntime.CreateProvider(context.Skills)];
        if (fileStore is not null)
        {
            var selectedFileToolNames = HarnessToolEffects.Register(
                context.ToolEffects,
                workspace!.FileTools
            );
            if (selectedFileToolNames.Contains(FileAccessProvider.ReadFileToolName))
            {
                WorkspaceFileReadTools.Add(context.ChatOptions, workspace.Path);
            }
            if (selectedFileToolNames.Contains(FileAccessProvider.GrepToolName))
            {
                WorkspaceGrepTools.Add(context.ChatOptions, workspace.Path);
            }
            if (selectedFileToolNames.Contains(FileAccessProvider.LsToolName))
            {
                WorkspaceListTools.Add(context.ChatOptions, workspace.Path);
            }

            var mafFileToolNames = selectedFileToolNames
                .Where(name =>
                    name != FileAccessProvider.GrepToolName
                    && name != FileAccessProvider.LsToolName
                    && name != FileAccessProvider.ReadFileToolName
                )
                .ToHashSet(StringComparer.Ordinal);
            if (mafFileToolNames.Count > 0)
            {
                providers.Add(
                    new FilteringAIContextProvider(
                        new FileAccessProvider(
                            fileStore,
                            new FileAccessProviderOptions
                            {
                                DisableWriteTools = !workspace.FileTools.Any(IsMutation),
                                DisableReadOnlyToolApproval = true,
                                DisableWriteToolApproval = true,
                            }
                        ),
                        mafFileToolNames
                    )
                );
            }
            if (workspace.IncludeGitReadOnly)
            {
                var existingToolCount = context.ChatOptions.Tools?.Count ?? 0;
                ReadOnlyGitTools.Add(context.ChatOptions, workspace.Path, context.ToolEffects);
                WorkspaceShellTools.Add(context.ChatOptions, workspace, context.ToolEffects);
                var workspaceTools =
                    context.ChatOptions.Tools?.Skip(existingToolCount).ToArray() ?? [];
                if (workspaceTools.Length > 0)
                {
                    context.ChatOptions.Tools = context
                        .ChatOptions.Tools?.Take(existingToolCount)
                        .ToList();
                    providers.Add(new StaticToolsAIContextProvider(workspaceTools));
                }
            }
            else
            {
                var existingToolCount = context.ChatOptions.Tools?.Count ?? 0;
                WorkspaceShellTools.Add(context.ChatOptions, workspace, context.ToolEffects);
                var workspaceTools =
                    context.ChatOptions.Tools?.Skip(existingToolCount).ToArray() ?? [];
                if (workspaceTools.Length > 0)
                {
                    context.ChatOptions.Tools = context
                        .ChatOptions.Tools?.Take(existingToolCount)
                        .ToList();
                    providers.Add(new StaticToolsAIContextProvider(workspaceTools));
                }
            }
            WorkspaceFileMutationTools.Add(context.ChatOptions, workspace, context.ToolEffects);
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

    private static bool IsMutation(WorkspaceToolKind kind) =>
        kind
            is WorkspaceToolKind.WriteFile
                or WorkspaceToolKind.DeleteFile
                or WorkspaceToolKind.Replace
                or WorkspaceToolKind.ReplaceLines
                or WorkspaceToolKind.CopyFile
                or WorkspaceToolKind.MoveFile
                or WorkspaceToolKind.CreateDirectory;
}

internal static class HarnessToolEffects
{
    internal static IReadOnlySet<string> Register(
        ToolEffectRegistry registry,
        IReadOnlySet<WorkspaceToolKind> selected
    )
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var kind in selected)
        {
            var (name, effect, evidence) = kind switch
            {
                WorkspaceToolKind.ReadFile => (
                    FileAccessProvider.ReadFileToolName,
                    Infrastructure.ToolEffect.Read,
                    Infrastructure.ToolEvidence.RepositoryInspection
                ),
                WorkspaceToolKind.ListFiles => (
                    FileAccessProvider.LsToolName,
                    Infrastructure.ToolEffect.Read,
                    Infrastructure.ToolEvidence.RepositoryInspection
                ),
                WorkspaceToolKind.Grep => (
                    FileAccessProvider.GrepToolName,
                    Infrastructure.ToolEffect.Read,
                    Infrastructure.ToolEvidence.RepositoryInspection
                ),
                WorkspaceToolKind.WriteFile => (
                    FileAccessProvider.WriteToolName,
                    Infrastructure.ToolEffect.WorkspaceMutation,
                    Infrastructure.ToolEvidence.None
                ),
                WorkspaceToolKind.DeleteFile => (
                    FileAccessProvider.DeleteFileToolName,
                    Infrastructure.ToolEffect.WorkspaceMutation,
                    Infrastructure.ToolEvidence.None
                ),
                WorkspaceToolKind.Replace => (
                    FileAccessProvider.ReplaceToolName,
                    Infrastructure.ToolEffect.WorkspaceMutation,
                    Infrastructure.ToolEvidence.None
                ),
                WorkspaceToolKind.ReplaceLines => (
                    FileAccessProvider.ReplaceLinesToolName,
                    Infrastructure.ToolEffect.WorkspaceMutation,
                    Infrastructure.ToolEvidence.None
                ),
                WorkspaceToolKind.CopyFile
                or WorkspaceToolKind.MoveFile
                or WorkspaceToolKind.CreateDirectory => default,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            if (
                kind
                is WorkspaceToolKind.CopyFile
                    or WorkspaceToolKind.MoveFile
                    or WorkspaceToolKind.CreateDirectory
            )
            {
                continue;
            }
            registry.Add(name, effect, evidence);
            names.Add(name);
        }
        return names;
    }
}

internal static class WorkspaceFileReadTools
{
    internal static void Add(ChatOptions options, string workspacePath)
    {
        var tools = options.Tools?.ToList() ?? [];
        if (tools.Any(tool => tool.Name == FileAccessProvider.ReadFileToolName))
        {
            throw new InvalidOperationException(
                $"Agent already exposes tool '{FileAccessProvider.ReadFileToolName}'."
            );
        }
        tools.Add(
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
            )
        );
        options.Tools = tools;
    }
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

    internal static void Add(
        ChatOptions options,
        ResolvedAgentWorkspace workspace,
        ToolEffectRegistry effects
    )
    {
        var tools = options.Tools?.ToList() ?? [];
        if (workspace.FileTools.Contains(WorkspaceToolKind.CopyFile))
        {
            tools.Add(
                AIFunctionFactory.Create(
                    (
                        string sourceFileName,
                        string destinationFileName,
                        bool overwrite,
                        CancellationToken cancellationToken
                    ) =>
                        Copy(
                            workspace.Path,
                            sourceFileName,
                            destinationFileName,
                            overwrite,
                            cancellationToken
                        ),
                    CopyToolName,
                    "Copy an existing file byte-for-byte within the configured workspace."
                )
            );
            effects.Add(CopyToolName, Infrastructure.ToolEffect.WorkspaceMutation);
        }
        if (workspace.FileTools.Contains(WorkspaceToolKind.MoveFile))
        {
            tools.Add(
                AIFunctionFactory.Create(
                    (
                        string sourceFileName,
                        string destinationFileName,
                        bool overwrite,
                        CancellationToken cancellationToken
                    ) =>
                        Move(
                            workspace.Path,
                            sourceFileName,
                            destinationFileName,
                            overwrite,
                            cancellationToken
                        ),
                    MoveToolName,
                    "Move an existing file byte-for-byte within the configured workspace."
                )
            );
            effects.Add(MoveToolName, Infrastructure.ToolEffect.WorkspaceMutation);
        }
        if (workspace.FileTools.Contains(WorkspaceToolKind.CreateDirectory))
        {
            tools.Add(
                AIFunctionFactory.Create(
                    (string directoryName, CancellationToken cancellationToken) =>
                        CreateDirectory(workspace.Path, directoryName, cancellationToken),
                    CreateDirectoryToolName,
                    "Create a directory and any missing parent directories within the configured workspace."
                )
            );
            effects.Add(CreateDirectoryToolName, Infrastructure.ToolEffect.WorkspaceMutation);
        }
        options.Tools = tools;
    }

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
        var tools = options.Tools?.ToList() ?? [];
        foreach (var registration in workspace.RegisteredTools ?? [])
        {
            var tool = registration.Create(workspace.Path);
            if (!string.Equals(tool.Name, registration.Name, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Registered workspace tool '{registration.Name}' created tool '{tool.Name}'."
                );
            }
            if (tools.Any(existing => existing.Name == tool.Name))
            {
                throw new InvalidOperationException($"Agent already exposes tool '{tool.Name}'.");
            }
            tools.Add(tool);
            effects.Add(tool.Name, registration.Effect, registration.Evidence);
        }
        options.Tools = tools;
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
        var tools = options.Tools?.ToList() ?? [];
        foreach (var command in workspace.Commands)
        {
            tools.Add(
                new WorkspaceCommandFunction(command, workspace.Path, timeout, maxOutputBytes)
            );
            effects.Add(
                command.Name,
                Infrastructure.ToolEffect.ProcessExecution,
                resultEvidence: ToProcessEvidence
            );
        }
        if (workspace.IncludeShell)
        {
            var tool = CreateExecutor(
                    workspace.Path,
                    acknowledgeUnsafe: true,
                    timeout,
                    maxOutputBytes
                )
                .AsAIFunction(
                    "run_shell",
                    "Run a model-authored command in the configured workspace without approval.",
                    requireApproval: false
                );
            tools.Add(tool);
            effects.Add(tool.Name, Infrastructure.ToolEffect.ProcessExecution);
        }
        options.Tools = tools;
    }

    private sealed class WorkspaceCommandFunction : AIFunction
    {
        private readonly AgentCommandDescriptor _command;
        private readonly string _workspacePath;
        private readonly TimeSpan? _timeout;
        private readonly int _maxOutputBytes;
        private readonly JsonElement _schema;

        internal WorkspaceCommandFunction(
            AgentCommandDescriptor command,
            string workspacePath,
            TimeSpan? timeout,
            int maxOutputBytes
        )
        {
            _command = command;
            _workspacePath = workspacePath;
            _timeout = timeout;
            _maxOutputBytes = maxOutputBytes;
            _schema = CreateSchema();
        }

        public override string Name => _command.Name;
        public override string Description => _command.Description;
        public override JsonElement JsonSchema => _schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            foreach (var key in arguments.Keys)
            {
                if (
                    _command.Arguments.Count == 0
                    || !string.Equals(key, "arguments", StringComparison.OrdinalIgnoreCase)
                )
                {
                    throw new ArgumentException(
                        $"Command '{_command.Name}' does not accept an argument named '{key}'.",
                        nameof(arguments)
                    );
                }
            }
            var command = new StringBuilder(_command.Command);
            if (arguments.TryGetValue("arguments", out var rawArguments))
            {
                var values = ReadStringArray(_command, rawArguments);
                Func<string, string> quote = OperatingSystem.IsWindows()
                    ? QuotePowerShell
                    : QuotePosix;
                foreach (var value in values)
                {
                    command.Append(' ').Append(quote(value));
                }
            }

            var fileName =
                OperatingSystem.IsWindows() ? "powershell.exe"
                : OperatingSystem.IsMacOS() ? "/bin/zsh"
                : "/bin/bash";
            string[] processArguments = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-Command", command.ToString()]
                : ["-lc", command.ToString()];
            var result = await LocalProcess.RunAsync(
                new LocalProcessRequest(
                    fileName,
                    processArguments,
                    _workspacePath,
                    _timeout ?? TimeSpan.FromMinutes(10),
                    _maxOutputBytes
                ),
                cancellationToken
            );
            return JsonSerializer.SerializeToElement(
                new
                {
                    ExitCode = result.TimedOut ? 124 : result.ExitCode,
                    result.Stdout,
                    result.Stderr,
                    result.Duration,
                    result.TimedOut,
                    Truncated = result.StdoutTruncated || result.StderrTruncated,
                },
                TandemJson.CreateTypedContract()
            );
        }

        private static IReadOnlyList<string> ReadStringArray(
            AgentCommandDescriptor command,
            object? rawArguments
        )
        {
            if (rawArguments is null)
            {
                throw new ArgumentException(
                    $"Argument 'arguments' of command '{command.Name}' cannot be null.",
                    nameof(rawArguments)
                );
            }
            var element = rawArguments switch
            {
                JsonElement json => json,
                _ => JsonSerializer.SerializeToElement(
                    rawArguments,
                    TandemJson.CreateTypedContract()
                ),
            };
            if (element.ValueKind != JsonValueKind.Array)
            {
                throw new ArgumentException(
                    $"Argument 'arguments' of command '{command.Name}' must be an array of strings.",
                    nameof(rawArguments)
                );
            }
            var values = new List<string>();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ArgumentException(
                        $"Argument 'arguments' of command '{command.Name}' must contain only strings.",
                        nameof(rawArguments)
                    );
                }
                values.Add(item.GetString()!);
            }
            if (values.Count > AgentCommand.MaximumArgumentCount)
            {
                throw new ArgumentException(
                    $"Command '{command.Name}' accepts at most {AgentCommand.MaximumArgumentCount} arguments."
                );
            }
            foreach (var value in values)
            {
                if (value.Length > AgentCommand.MaximumArgumentLength)
                {
                    throw new ArgumentException(
                        $"Argument of command '{command.Name}' must be at most {AgentCommand.MaximumArgumentLength} characters."
                    );
                }
            }
            return values;
        }

        private JsonElement CreateSchema()
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (_command.Arguments.Count > 0)
            {
                properties["arguments"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "string",
                    },
                    ["description"] = _command.Description,
                };
            }
            return JsonSerializer.SerializeToElement(
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = properties,
                    ["additionalProperties"] = false,
                }
            );
        }

        // MAF uses these same dialect-specific forms internally, but does not expose them publicly.
        private static string QuotePowerShell(string value) =>
            $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

        private static string QuotePosix(string value) =>
            $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    private static ToolResultEvidenceDescriptor.Process? ToProcessEvidence(object? result)
    {
        if (result is ShellResult shellResult)
        {
            return new ToolResultEvidenceDescriptor.Process(
                shellResult.ExitCode,
                shellResult.Stdout,
                shellResult.Stderr,
                shellResult.Duration,
                shellResult.TimedOut,
                shellResult.Truncated
            );
        }
        if (result is not JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        try
        {
            var evidence = element.Deserialize<ShellResultEvidence>(
                TandemJson.CreateTypedContract()
            );
            return evidence is null
                ? null
                : new ToolResultEvidenceDescriptor.Process(
                    evidence.ExitCode,
                    evidence.Stdout ?? string.Empty,
                    evidence.Stderr ?? string.Empty,
                    evidence.Duration,
                    evidence.TimedOut,
                    evidence.Truncated
                );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ShellResultEvidence(
        int ExitCode,
        string? Stdout,
        string? Stderr,
        TimeSpan Duration,
        bool TimedOut,
        bool Truncated
    );

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

internal sealed class BomlessFileSystemAgentFileStore(string rootPath) : AgentFileStore
{
    private static readonly UTF8Encoding _utf8WithoutBom = new(false);
    private readonly string _rootPath = Path.GetFullPath(rootPath);
    private readonly FileSystemAgentFileStore _inner = new(rootPath);

    public override async Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken
    )
    {
        var normalized = content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
        var fullPath = WorkspacePathAuthority.Resolve(_rootPath, path, "write");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, normalized, _utf8WithoutBom, cancellationToken);
    }

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken) =>
        _inner.ReadAsync(path, cancellationToken);

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken) =>
        _inner.DeleteAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken
    ) => _inner.ListChildrenAsync(directory, cancellationToken);

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken) =>
        _inner.FileExistsAsync(path, cancellationToken);

    public override Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken
    ) => _inner.SearchAsync(directory, regexPattern, globPattern, recursive, cancellationToken);

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        _inner.CreateDirectoryAsync(path, cancellationToken);
}

internal sealed class GitExcludedFileStore(AgentFileStore inner) : AgentFileStore
{
    private const string SearchTruncationMarker = "\n[...additional search results omitted...]";
    private const int MaximumSearchResults = 10;
    private const int MaximumMatchesPerResult = 5;
    private const int MaximumPathCharacters = 1024;
    private const int MaximumSnippetCharacters = 2048;
    private const int MaximumMatchCharacters = 1024;

    public override Task WriteAsync(
        string path,
        string content,
        CancellationToken cancellationToken
    )
    {
        RejectGitPath(path);
        var normalized = content.Length > 0 && content[0] == '\uFEFF' ? content[1..] : content;
        return inner.WriteAsync(path, normalized, cancellationToken);
    }

    public override Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return inner.ReadAsync(path, cancellationToken);
    }

    public override Task<bool> DeleteAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return inner.DeleteAsync(path, cancellationToken);
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken
    ) =>
        (await inner.ListChildrenAsync(directory, cancellationToken))
            .Where(entry => !string.Equals(entry.Name, ".git", StringComparison.OrdinalIgnoreCase))
            .ToList();

    public override Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return inner.FileExistsAsync(path, cancellationToken);
    }

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern,
        bool recursive,
        CancellationToken cancellationToken
    )
    {
        var results = (
            await inner.SearchAsync(
                directory,
                regexPattern,
                globPattern,
                recursive,
                cancellationToken
            )
        )
            .Where(result => !IsExcludedSearchResult(result))
            .ToList();
        var bounded = results
            .Take(MaximumSearchResults)
            .Select(result => new FileSearchResult
            {
                FileName = Truncate(result.FileName, MaximumPathCharacters),
                Snippet = Truncate(result.Snippet, MaximumSnippetCharacters),
                MatchingLines =
                [
                    .. result
                        .MatchingLines.Take(MaximumMatchesPerResult)
                        .Select(match => new FileSearchMatch
                        {
                            LineNumber = match.LineNumber,
                            Line = Truncate(match.Line, MaximumMatchCharacters),
                        }),
                ],
            })
            .ToList();
        if (
            results.Count > MaximumSearchResults
            || results.Any(result =>
                result.Snippet.Length > MaximumSnippetCharacters
                || result.MatchingLines.Count > MaximumMatchesPerResult
                || result.MatchingLines.Any(match => match.Line.Length > MaximumMatchCharacters)
            )
        )
        {
            bounded.Add(
                new FileSearchResult
                {
                    FileName = "[...additional search results omitted...]",
                    Snippet = "Narrow the directory, regex, or glob pattern for more results.",
                }
            );
        }
        return bounded;
    }

    internal static bool IsExcludedSearchResult(FileSearchResult result) =>
        WorkspaceSearchPolicy.HasExcludedDirectorySegment(result.FileName)
        || WorkspaceSearchPolicy.HasBinaryExtension(result.FileName)
        || LooksBinary(result.Snippet)
        || result.MatchingLines.Any(match => LooksBinary(match.Line));

    private static bool LooksBinary(string content)
    {
        if (content.IndexOf('\0') >= 0)
        {
            return true;
        }

        var suspicious = content.Count(character =>
            character == '\uFFFD'
            || char.IsControl(character) && character is not ('\r' or '\n' or '\t')
        );
        return suspicious >= 4 && suspicious * 100 >= content.Length;
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        RejectGitPath(path);
        return inner.CreateDirectoryAsync(path, cancellationToken);
    }

    private static void RejectGitPath(string path)
    {
        if (ContainsGitSegment(path))
        {
            throw new WorkspacePathException($"Access to '.git' paths is denied: {path}");
        }
    }

    private static bool ContainsGitSegment(string path) =>
        path.Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, ".git", StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : string.Concat(
                value.AsSpan(0, maximumCharacters - SearchTruncationMarker.Length),
                SearchTruncationMarker
            );
}

#pragma warning restore MAAI001
