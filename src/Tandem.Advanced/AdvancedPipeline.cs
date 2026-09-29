using System.Text.Json;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure;

namespace Tandem.Advanced;

public sealed record OperationResult<TState>(TState State, OperationOutcome Outcome)
{
    internal static OperationResult<TState> From(PipelineMessage<TState> message)
    {
        var outcome =
            message.LatestOutcome
            ?? throw new InvalidOperationException("Operation produced no outcome.");
        return new OperationResult<TState>(message.State, OperationOutcome.From(outcome));
    }
}

public sealed record OperationOutcome(
    string Kind,
    string StepId,
    string Summary,
    JsonElement Payload = default,
    TimeSpan Duration = default
)
{
    internal static OperationOutcome From(BlockOutcome outcome) =>
        new(outcome.Kind, outcome.StepId, outcome.Summary, outcome.Payload, outcome.Duration);

    internal BlockOutcome ToCore() => new(Kind, StepId, Summary, Payload, Duration);
}

public sealed class PipelineOperationContext<TState>
{
    private readonly PipelineMessage<TState> _message;

    internal PipelineOperationContext(PipelineMessage<TState> message)
    {
        _message = message;
        RunId = message.Runtime.RunId;
        State = message.State;
        LatestOutcome = message.LatestOutcome is { } outcome
            ? OperationOutcome.From(outcome)
            : null;
    }

    public Guid RunId { get; }
    public TState State { get; }
    public OperationOutcome? LatestOutcome { get; }

    public ValueTask ObserveCommandOutputAsync(
        string stepId,
        string command,
        string output,
        int exitCode,
        CancellationToken cancellationToken
    ) =>
        _message.RunContext?.ObserveAsync(
            new PipelineCommandOutput(RunId, stepId, command, output, exitCode),
            cancellationToken
        ) ?? ValueTask.CompletedTask;
}

public sealed record AgentMessageContext<TState>(
    Guid RunId,
    TState State,
    OperationOutcome? LatestOutcome
)
{
    internal static AgentMessageContext<TState> From(PipelineMessage<TState> message) =>
        new(
            message.Runtime.RunId,
            message.State,
            message.LatestOutcome is { } outcome ? OperationOutcome.From(outcome) : null
        );
}

public enum AgentConversationRetention
{
    Retain,
    Discard,
}

public sealed record AgentConversationDecision(AgentConversationRetention Retention);

public delegate AgentConversationDecision AgentConversationPolicy<TState>(
    AgentMessageContext<TState> context,
    OperationOutcome outcome
);

public abstract record ToolInterceptionResult
{
    public sealed record Blocked(string Message) : ToolInterceptionResult;
}

public sealed record ToolInvocation(string Name, ToolEffect Effect, JsonElement Arguments);

public delegate ValueTask<ToolInterceptionResult?> ToolInterceptor<TState>(
    AgentMessageContext<TState> context,
    ToolInvocation invocation,
    CancellationToken cancellationToken
);

public delegate ValueTask<string?> MessageAugmentation<TState>(
    AgentMessageContext<TState> context,
    CancellationToken cancellationToken
);

public sealed record AgentTurnObservation<TState>(
    AgentMessageContext<TState> Context,
    string AssistantText,
    IReadOnlyList<string> ToolNames,
    bool HasAcceptedLifecycleOutcome,
    int ContinuationAttempt
);

public sealed record AgentTurnDirective(string Prompt, string? RequiredToolName = null);

public delegate ValueTask<AgentTurnDirective?> AgentTurnContinuationPolicy<TState>(
    AgentTurnObservation<TState> observation,
    CancellationToken cancellationToken
);

public sealed record AgentTurnPolicy<TState>
{
    public AgentTurnPolicy(
        int maxContinuationAttempts,
        AgentTurnContinuationPolicy<TState> @continue
    )
    {
        if (maxContinuationAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContinuationAttempts));
        }

        MaxContinuationAttempts = maxContinuationAttempts;
        Continue = @continue;
    }

    public int MaxContinuationAttempts { get; }
    public AgentTurnContinuationPolicy<TState> Continue { get; }
}

public sealed record AgentProfileDecision(string ProfileName, string Reason);

public delegate AgentProfileDecision AgentProfilePolicy<TState>(TState state);

public sealed record AgentCheckpointContext<TState>(TState State, int CurrentContextTokens);

public enum CheckpointSessionBehavior
{
    Retain,
    Reset,
}

public sealed record CheckpointPolicy<TState>(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent,
    AgentCapability<TState> Capability,
    string Instructions,
    Func<AgentCheckpointContext<TState>, string> UserMessage,
    CheckpointSessionBehavior SessionBehavior = CheckpointSessionBehavior.Reset
)
{
    public bool DisableCompaction { get; init; }
}

public sealed record AgentStateGuard<TState>(
    string Id,
    Func<TState, bool> IsActive,
    IReadOnlySet<ToolEffect> Blocks,
    string Message,
    AgentCapability<TState>? Remediation = null
);

public sealed record AgentCommand
{
    internal const int MaximumArgumentCount = 16;
    internal const int MaximumArgumentLength = 200;

    private AgentCommand(
        string name,
        string description,
        string command,
        IReadOnlyList<string> arguments
    )
    {
        Name = name;
        Description = description;
        Command = command;
        Arguments = arguments;
    }

    public string Name { get; }
    public string Description { get; }
    public string Command { get; }
    public IReadOnlyList<string> Arguments { get; }

    public static AgentCommand Define(string name, string description, string command) =>
        Define(name, description, command, []);

    public static AgentCommand Define(
        string name,
        string description,
        string command,
        IReadOnlyList<string>? arguments
    )
    {
        ValidateToolName(name, nameof(name));
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        arguments ??= [];
        if (arguments.Count > MaximumArgumentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(arguments),
                arguments.Count,
                $"A command accepts at most {MaximumArgumentCount} arguments."
            );
        }
        var copy = arguments.Select(ValidateArgument).ToArray();
        return new AgentCommand(name, description, command, copy);
    }

    internal AgentCommandDescriptor ToDescriptor() => new(Name, Description, Command, Arguments);

    private static string ValidateArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new ArgumentOutOfRangeException(
                nameof(argument),
                argument,
                "A command argument must not be blank."
            );
        }
        if (argument.Length > MaximumArgumentLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(argument),
                argument.Length,
                $"A command argument must be at most {MaximumArgumentLength} characters."
            );
        }
        return argument;
    }

    internal static void ValidateToolName(string name, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, parameterName);
        if (
            name.Length > 64
            || !(char.IsAsciiLetter(name[0]) || name[0] == '_')
            || name.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            )
        )
        {
            throw new ArgumentException(
                "Tool names must contain at most 64 ASCII letters, digits, underscores, or hyphens and start with a letter or underscore.",
                parameterName
            );
        }
    }
}

public sealed class AgentToolSelection
{
    internal AgentToolSelection(AgentToolSelectionDescriptor descriptor, object? owner = null)
    {
        Descriptor = descriptor;
        Owner = owner;
    }

    internal AgentToolSelectionDescriptor Descriptor { get; }
    internal object? Owner { get; }

    public static implicit operator AgentToolSelection(string name) => AgentTools.Select(name);
}

public sealed class AgentWorkspaceTool
{
    private AgentWorkspaceTool(
        string name,
        Func<string, AIFunction> create,
        ToolEffect effect,
        ToolEvidence evidence
    )
    {
        Name = name;
        Create = create;
        Effect = effect;
        Evidence = evidence;
    }

    internal string Name { get; }
    internal Func<string, AIFunction> Create { get; }
    internal ToolEffect Effect { get; }
    internal ToolEvidence Evidence { get; }

    public static AgentWorkspaceTool Define(
        string name,
        Func<string, AIFunction> create,
        ToolEffect effect,
        ToolEvidence evidence = ToolEvidence.None
    )
    {
        AgentCommand.ValidateToolName(name, nameof(name));
        ArgumentNullException.ThrowIfNull(create);
        if (effect == ToolEffect.Unclassified)
        {
            throw new ArgumentOutOfRangeException(
                nameof(effect),
                "Registered workspace tools require a classified effect."
            );
        }
        return new AgentWorkspaceTool(name, create, effect, evidence);
    }
}

public sealed class AgentToolGroup<TState>
{
    internal AgentToolGroup(
        AgentToolGroupDescriptor<TState> descriptor,
        IReadOnlyList<object?> owners
    )
    {
        Descriptor = descriptor;
        Owners = owners;
    }

    internal AgentToolGroupDescriptor<TState> Descriptor { get; }
    internal IReadOnlyList<object?> Owners { get; }
}

public static class AgentTools
{
    public static AgentToolGroup<TState> Always<TState>(params AgentToolSelection[] tools) =>
        Create<TState>(_ => true, tools);

    public static AgentToolGroup<TState> When<TState>(
        Func<TState, bool> predicate,
        params AgentToolSelection[] tools
    ) => Create(predicate, tools);

    internal static AgentToolSelection Select(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!BuiltInAgentTools.IsSelectable(name))
        {
            throw new ArgumentException($"Unknown agent workspace tool '{name}'.", nameof(name));
        }
        return new AgentToolSelection(
            new AgentToolSelectionDescriptor(AgentToolSelectionKind.BuiltIn, name)
        );
    }

    private static AgentToolGroup<TState> Create<TState>(
        Func<TState, bool> predicate,
        IReadOnlyList<AgentToolSelection> tools
    )
    {
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0)
        {
            throw new ArgumentException(
                "A tool group must select at least one tool.",
                nameof(tools)
            );
        }
        if (tools.Any(tool => tool is null))
        {
            throw new ArgumentException(
                "A tool group cannot contain null selections.",
                nameof(tools)
            );
        }
        var descriptors = tools.Select(tool => tool.Descriptor).ToArray();
        var duplicate = descriptors
            .GroupBy(tool => (tool.Kind, tool.Name))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                "A tool group cannot select the same tool twice.",
                nameof(tools)
            );
        }
        return new AgentToolGroup<TState>(
            new AgentToolGroupDescriptor<TState>(predicate, descriptors),
            tools.Select(tool => tool.Owner).ToArray()
        );
    }
}

public sealed class AgentWorkspace<TState>
{
    private readonly Dictionary<string, AgentWorkspaceTool> _registeredTools = new(
        StringComparer.Ordinal
    );

    private AgentWorkspace(
        Func<TState, string> path,
        Func<TState, IReadOnlyList<AgentCommand>> commands
    )
    {
        Path = path;
        CommandFactory = commands;
        Commands = new AgentToolSelection(
            new AgentToolSelectionDescriptor(AgentToolSelectionKind.Commands),
            this
        );
    }

    internal Func<TState, string> Path { get; }
    internal Func<TState, IReadOnlyList<AgentCommand>> CommandFactory { get; }
    internal IReadOnlyDictionary<string, AgentWorkspaceTool> RegisteredTools => _registeredTools;
    public AgentToolSelection Commands { get; }

    public AgentToolSelection Register(AgentWorkspaceTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        if (!_registeredTools.TryAdd(tool.Name, tool))
        {
            throw new ArgumentException(
                $"Workspace tool '{tool.Name}' is already registered.",
                nameof(tool)
            );
        }
        return new AgentToolSelection(
            new AgentToolSelectionDescriptor(AgentToolSelectionKind.Registered, tool.Name),
            this
        );
    }

    public static AgentWorkspace<TState> Define(
        Func<TState, string> path,
        IReadOnlyList<AgentCommand> commands
    )
    {
        ArgumentNullException.ThrowIfNull(commands);
        var snapshot = commands.ToArray();
        ValidateCommands(snapshot);
        return Define(path, _ => snapshot);
    }

    public static AgentWorkspace<TState> Define(
        Func<TState, string> path,
        Func<TState, IReadOnlyList<AgentCommand>> commands
    )
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(commands);
        return new AgentWorkspace<TState>(path, commands);
    }

    internal static void ValidateCommands(IReadOnlyList<AgentCommand> commands)
    {
        if (commands.Any(command => command is null))
        {
            throw new InvalidOperationException(
                "A workspace command catalogue cannot contain null commands."
            );
        }
        var duplicate = commands
            .GroupBy(command => command.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Workspace command '{duplicate.Key}' is declared more than once."
            );
        }
    }
}

public static class AgentProfiles
{
    public static AgentBuilder<TState> Create<TState>(
        string id,
        string profile,
        string instructions,
        IChatClient chatClient,
        Func<string, IChatClient> profileChatClients
    ) =>
        AgentBuilder<TState>.CreateProfiled(
            id,
            profile,
            instructions,
            chatClient,
            profileChatClients
        );
}

public static class AdvancedAgentBuilderExtensions
{
    public static AgentBuilder<TState> UseHarness<TState>(
        this AgentBuilder<TState> builder,
        string harnessInstructions
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessInstructions);
        return builder.ConfigureImplementation(context =>
            HarnessAgentImplementation.Create(context, harnessInstructions)
        );
    }

    public static AgentBuilder<TState> UseHarness<TState>(
        this AgentBuilder<TState> builder,
        string harnessInstructions,
        int maxContextWindowTokens,
        int maxOutputTokens,
        bool disableCompaction = false
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessInstructions);
        if (maxContextWindowTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContextWindowTokens));
        }

        if (maxOutputTokens <= 0 || maxOutputTokens >= maxContextWindowTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(maxOutputTokens));
        }

        return builder
            .ConfigureContextBudget(maxContextWindowTokens, maxOutputTokens, disableCompaction)
            .UseHarness(harnessInstructions);
    }

    public static AgentBuilder<TState> WithWorkspace<TState>(
        this AgentBuilder<TState> builder,
        AgentWorkspace<TState> workspace,
        IReadOnlyList<AgentToolGroup<TState>> tools,
        ToolInterceptor<TState>? toolInterceptor = null
    )
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(tools);
        if (tools.Count == 0)
        {
            throw new ArgumentException(
                "A workspace requires at least one tool group.",
                nameof(tools)
            );
        }
        var groups = tools.Select(group => group?.Descriptor).ToArray();
        if (groups.Any(group => group is null))
        {
            throw new ArgumentException(
                "Workspace tool groups cannot contain null values.",
                nameof(tools)
            );
        }
        if (
            tools
                .SelectMany(group => group.Owners)
                .Any(owner => owner is not null && !ReferenceEquals(owner, workspace))
        )
        {
            throw new ArgumentException(
                "A workspace cannot select commands from another workspace.",
                nameof(tools)
            );
        }
        var selections = groups.SelectMany(group => group!.Tools).ToArray();
        var duplicate = selections
            .GroupBy(selection => (selection.Kind, selection.Name))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                "A workspace cannot select the same effective tool in more than one group.",
                nameof(tools)
            );
        }
        return builder.ConfigureWorkspace(
            new AgentWorkspaceDescriptor<TState>(
                workspace.Path,
                state =>
                {
                    var commands = workspace.CommandFactory(state);
                    ArgumentNullException.ThrowIfNull(commands);
                    AgentWorkspace<TState>.ValidateCommands(commands);
                    return commands.Select(command => command.ToDescriptor()).ToArray();
                },
                groups!,
                workspace.RegisteredTools.ToDictionary(
                    entry => entry.Key,
                    entry => new AgentWorkspaceToolDescriptor(
                        entry.Value.Name,
                        entry.Value.Create,
                        entry.Value.Effect,
                        entry.Value.Evidence
                    ),
                    StringComparer.Ordinal
                )
            ),
            toolInterceptor is null
                ? null
                : async (message, toolName, effect, arguments, cancellationToken) =>
                {
                    var result = await toolInterceptor(
                        AgentMessageContext<TState>.From(message),
                        new ToolInvocation(toolName, effect, arguments.Clone()),
                        cancellationToken
                    );
                    return result is ToolInterceptionResult.Blocked blocked
                        ? blocked.Message
                        : null;
                }
        );
    }

    public static AgentBuilder<TState> RequireOutputAcceptance<TState, TOutput>(
        this AgentBuilder<TState> builder,
        OutputAcceptancePolicy<TState, TOutput> acceptance
    )
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        return builder.ConfigureOutputAcceptance<TOutput>(
            (attempt, output, _) =>
                ValueTask.FromResult(
                    acceptance(OutputAcceptanceObservation<TState, TOutput>.From(attempt, output))
                )
        );
    }

    public static AgentBuilder<TState> WithOutputAcceptance<TState, TOutput>(
        this AgentBuilder<TState> builder,
        OutputAcceptance<TState, TOutput> acceptance
    )
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        return builder.ConfigureOutputAcceptance<TOutput>(
            async (attempt, output, cancellationToken) =>
            {
                await acceptance(
                    OutputAcceptanceObservation<TState, TOutput>.From(attempt, output),
                    cancellationToken
                );
                return [];
            }
        );
    }

    public static AgentBuilder<TState> WithCheckpoint<TState>(
        this AgentBuilder<TState> builder,
        CheckpointPolicy<TState> policy
    )
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.ContextWindowTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.ContextWindowTokens));
        }
        if (policy.MaxOutputTokens <= 0 || policy.MaxOutputTokens >= policy.ContextWindowTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.MaxOutputTokens));
        }
        if (policy.CheckpointAtPercent is <= 0 or >= 100)
        {
            throw new ArgumentOutOfRangeException(nameof(policy.CheckpointAtPercent));
        }
        ArgumentNullException.ThrowIfNull(policy.Capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(policy.Instructions);
        ArgumentNullException.ThrowIfNull(policy.UserMessage);
        var resetSessionAfterRelease = policy.SessionBehavior switch
        {
            CheckpointSessionBehavior.Retain => false,
            CheckpointSessionBehavior.Reset => true,
            _ => throw new ArgumentOutOfRangeException(nameof(policy.SessionBehavior)),
        };

        return builder.ConfigureCheckpoint(
            new AgentCheckpointDescriptor<TState>(
                policy.ContextWindowTokens,
                policy.MaxOutputTokens,
                policy.CheckpointAtPercent,
                policy.Capability.Descriptor,
                policy.Instructions,
                (state, currentContextTokens) =>
                    policy.UserMessage(
                        new AgentCheckpointContext<TState>(state, currentContextTokens)
                    ),
                resetSessionAfterRelease,
                policy.DisableCompaction
            )
        );
    }

    public static AgentBuilder<TState> WithStateGuard<TState>(
        this AgentBuilder<TState> builder,
        AgentStateGuard<TState> guard
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guard.Id);
        ArgumentNullException.ThrowIfNull(guard.IsActive);
        ArgumentNullException.ThrowIfNull(guard.Blocks);
        ArgumentException.ThrowIfNullOrWhiteSpace(guard.Message);
        if (guard.Blocks.Count == 0 || guard.Blocks.Contains(ToolEffect.Unclassified))
        {
            throw new ArgumentException(
                "State guards require at least one classified blocked effect.",
                nameof(guard)
            );
        }
        return builder.ConfigureStateGuard(
            new AgentStateGuardDescriptor<TState>(
                guard.Id,
                guard.IsActive,
                guard.Blocks.ToHashSet(),
                guard.Message,
                guard.Remediation?.Descriptor.ToolName
            )
        );
    }

    public static AgentBuilder<TState> WithMessageAugmentation<TState>(
        this AgentBuilder<TState> builder,
        MessageAugmentation<TState> augmentation
    ) =>
        builder.ConfigureMessageAugmentation(
            (message, cancellationToken) =>
                augmentation(AgentMessageContext<TState>.From(message), cancellationToken)
        );

    public static AgentBuilder<TState> WithContinuationPolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentTurnPolicy<TState> policy
    ) =>
        builder.ConfigureContinuationPolicy(
            new AgentTurnDescriptor<TState>(
                policy.MaxContinuationAttempts,
                async (
                    message,
                    assistantText,
                    toolNames,
                    hasAcceptedLifecycleOutcome,
                    continuationAttempt,
                    cancellationToken
                ) =>
                {
                    var directive = await policy.Continue(
                        new AgentTurnObservation<TState>(
                            AgentMessageContext<TState>.From(message),
                            assistantText,
                            toolNames,
                            hasAcceptedLifecycleOutcome,
                            continuationAttempt
                        ),
                        cancellationToken
                    );
                    return directive is null
                        ? null
                        : new AgentTurnDirectiveDescriptor(
                            directive.Prompt,
                            directive.RequiredToolName
                        );
                }
            )
        );

    public static AgentBuilder<TState> WithProfilePolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentProfilePolicy<TState> policy
    ) =>
        builder.ConfigureProfilePolicy(state =>
        {
            var decision = policy(state);
            return new AgentProfileSelection(decision.ProfileName, decision.Reason);
        });

    public static AgentBuilder<TState> WithConversationPolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentConversationPolicy<TState> policy
    ) =>
        builder.ConfigureConversationPolicy(
            (message, outcome) =>
                policy(
                    AgentMessageContext<TState>.From(message),
                    OperationOutcome.From(outcome)
                ).Retention == AgentConversationRetention.Retain
        );
}

public static class PipelineOperation
{
    public static async ValueTask<Outcome<TState>> RunOutcomeAsync<TState>(
        TState state,
        Func<PipelineOperationContext<TState>, ValueTask<OperationResult<TState>>> execute,
        Func<OperationResult<TState>, Outcome<TState>> map
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var pipeline = PipelineExecutionEnvelope.Get(state);
        var result = await execute(new PipelineOperationContext<TState>(pipeline));
        PipelineExecutionEnvelope.Set(
            pipeline with
            {
                State = result.State,
                LatestOutcome = result.Outcome.ToCore(),
            }
        );
        return map(result);
    }
}
