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
            var mafFileToolNames = selectedFileToolNames
                .Where(name => name != FileAccessProvider.GrepToolName)
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
                    [System.ComponentModel.Description("Zero-based UTF-16 text offset.")]
                        int offset = 0,
                    [System.ComponentModel.Description(
                        "Maximum UTF-16 code units to return, from 1 to 65536."
                    )]
                        int limit = BoundedTextPageReader.DefaultLimit,
                    CancellationToken cancellationToken = default
                ) =>
                    await BoundedTextPageReader.ReadAsync(
                        WorkspacePathAuthority.Resolve(workspacePath, path, "read"),
                        offset,
                        limit,
                        cancellationToken
                    ),
                FileAccessProvider.ReadFileToolName,
                "Read a bounded page of a workspace text file. Follow nextOffset until hasMore is false. Offsets and lengths are UTF-16 code units."
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
            throw new UnauthorizedAccessException("File paths must be relative to the workspace.");
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
            throw new UnauthorizedAccessException("File paths must remain within the workspace.");
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
            throw new UnauthorizedAccessException("Access to Git metadata is not allowed.");
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
                    throw new UnauthorizedAccessException(
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
        File.Copy(source, destination, overwrite);
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
        File.Move(source, destination, overwrite);
        return $"Moved '{sourceFileName}' to '{destinationFileName}'.";
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
        int maxOutputBytes = 64 * 1024
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
        private static readonly TimeSpan _regexTimeout = TimeSpan.FromSeconds(1);
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
            _schema = CreateSchema(command.Arguments);
        }

        public override string Name => _command.Name;
        public override string Description => _command.Description;
        public override JsonElement JsonSchema => _schema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken
        )
        {
            var declarations = _command.Arguments.ToDictionary(
                value => value.Name,
                StringComparer.Ordinal
            );
            foreach (var name in arguments.Keys)
            {
                if (!declarations.ContainsKey(name))
                {
                    throw new ArgumentException($"Unknown argument '{name}'.", nameof(arguments));
                }
            }

            var command = new StringBuilder(_command.Command);
            foreach (var declaration in _command.Arguments)
            {
                if (!arguments.TryGetValue(declaration.Name, out var rawValue))
                {
                    continue;
                }
                var value = rawValue switch
                {
                    string text => text,
                    JsonElement { ValueKind: JsonValueKind.String } element => element.GetString()!,
                    null => throw new ArgumentException(
                        $"Argument '{declaration.Name}' cannot be null.",
                        nameof(arguments)
                    ),
                    _ => throw new ArgumentException(
                        $"Argument '{declaration.Name}' must be a string.",
                        nameof(arguments)
                    ),
                };
                ValidateValue(declaration, value);
                command
                    .Append(' ')
                    .Append(declaration.Flag)
                    .Append(' ')
                    .Append(
                        OperatingSystem.IsWindows() ? QuotePowerShell(value) : QuotePosix(value)
                    );
            }

            await using var shell = CreateExecutor(
                _workspacePath,
                acknowledgeUnsafe: false,
                _timeout,
                _maxOutputBytes
            );
            var result = await shell.RunAsync(command.ToString(), cancellationToken);
            var serializerOptions = TandemJson.CreateTypedContract();
            serializerOptions.WriteIndented = true;
            return JsonSerializer.SerializeToElement(result, serializerOptions);
        }

        private static void ValidateValue(AgentCommandArgumentDescriptor declaration, string value)
        {
            if (declaration.MaxLength is { } maximum && value.Length > maximum)
            {
                throw new ArgumentException(
                    $"Argument '{declaration.Name}' exceeds its maximum length."
                );
            }
            if (
                declaration.Pattern is { } pattern
                && !System.Text.RegularExpressions.Regex.IsMatch(
                    value,
                    $"\\A(?:{pattern})\\z",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                    _regexTimeout
                )
            )
            {
                throw new ArgumentException(
                    $"Argument '{declaration.Name}' does not match its pattern."
                );
            }
            if (
                declaration.AllowedValues is { } allowed
                && !allowed.Contains(value, StringComparer.Ordinal)
            )
            {
                throw new ArgumentException(
                    $"Argument '{declaration.Name}' is not an allowed value."
                );
            }
        }

        private static JsonElement CreateSchema(
            IReadOnlyList<AgentCommandArgumentDescriptor> arguments
        )
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var argument in arguments)
            {
                var property = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "string",
                    ["description"] = argument.Description,
                };
                if (argument.MaxLength is { } maximum)
                {
                    property["maxLength"] = maximum;
                }
                if (argument.Pattern is { } pattern)
                {
                    property["pattern"] = $"^(?:{pattern})$";
                }
                else
                {
                    property["enum"] = argument.AllowedValues;
                }
                properties.Add(argument.Name, property);
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
        await _inner.WriteAsync(path, normalized, cancellationToken);
        var fullPath = Path.GetFullPath(Path.Combine(_rootPath, path));
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
    private static readonly HashSet<string> _excludedSearchDirectories = new(
        [
            ".angular",
            ".build",
            ".bundle",
            ".cache",
            ".dart_tool",
            ".eggs",
            ".expo",
            ".git",
            ".gradle",
            ".hg",
            ".idea",
            ".mypy_cache",
            ".next",
            ".nox",
            ".nuxt",
            ".nx",
            ".nyc_output",
            ".output",
            ".parcel-cache",
            ".pytest_cache",
            ".pnpm-store",
            ".ruff_cache",
            ".sass-cache",
            ".serverless",
            ".stack-work",
            ".svelte-kit",
            ".svn",
            ".tox",
            ".turbo",
            ".terraform",
            ".terragrunt-cache",
            ".venv",
            ".vite",
            ".vs",
            ".yarn",
            "_build",
            "__pycache__",
            "artifacts",
            "bin",
            "bower_components",
            "Binaries",
            "build",
            "coverage",
            "Carthage",
            "CMakeFiles",
            "deps",
            "DerivedData",
            "DerivedDataCache",
            "dist",
            "env",
            "jspm_packages",
            "Intermediate",
            "Library",
            "node_modules",
            "obj",
            "out",
            "packages",
            "Pods",
            "Saved",
            "site-packages",
            "target",
            "TestResults",
            "tmp",
            "storybook-static",
            "venv",
            "vendor",
        ],
        StringComparer.OrdinalIgnoreCase
    );
    private static readonly HashSet<string> _binarySearchExtensions = new(
        [
            ".7z",
            ".a",
            ".apk",
            ".avi",
            ".bin",
            ".bmp",
            ".bz2",
            ".class",
            ".db",
            ".deb",
            ".dmg",
            ".dll",
            ".dylib",
            ".ear",
            ".exe",
            ".flac",
            ".gif",
            ".gem",
            ".gz",
            ".ico",
            ".ipa",
            ".iso",
            ".jar",
            ".jpeg",
            ".jpg",
            ".mov",
            ".mp3",
            ".mp4",
            ".o",
            ".otf",
            ".nupkg",
            ".pdf",
            ".pdb",
            ".png",
            ".pyc",
            ".pyo",
            ".rar",
            ".rpm",
            ".so",
            ".sqlite",
            ".sqlite3",
            ".snupkg",
            ".tar",
            ".tgz",
            ".ttf",
            ".war",
            ".wasm",
            ".wav",
            ".webm",
            ".webp",
            ".whl",
            ".woff",
            ".woff2",
            ".xz",
            ".zip",
        ],
        StringComparer.OrdinalIgnoreCase
    );
    private const string SearchTruncationMarker = "\n[...additional search results omitted...]";
    private const int MaximumListEntries = 200;
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
            .Take(MaximumListEntries)
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
        HasExcludedDirectory(result.FileName)
        || HasBinaryExtension(result.FileName)
        || LooksBinary(result.Snippet)
        || result.MatchingLines.Any(match => LooksBinary(match.Line));

    private static bool HasExcludedDirectory(string path)
    {
        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 1
            && segments[..^1]
                .Any(segment =>
                    _excludedSearchDirectories.Contains(segment)
                    || segment.StartsWith("bazel-", StringComparison.OrdinalIgnoreCase)
                    || segment.StartsWith("cmake-build-", StringComparison.OrdinalIgnoreCase)
                );
    }

    private static bool HasBinaryExtension(string path)
    {
        var name = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return name is not null && _binarySearchExtensions.Contains(Path.GetExtension(name));
    }

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
            throw new UnauthorizedAccessException($"Access to '.git' paths is denied: {path}");
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
