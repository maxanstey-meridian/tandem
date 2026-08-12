using Tandem.Infrastructure;

namespace Tandem.Domain;

internal sealed record AgentBlockConfig<TState>(
    string StepId,
    string ProfileName,
    string SystemInstructions,
    IReadOnlyList<AgentCapabilityDescriptor<TState>> Capabilities,
    Func<TState, string>? UserMessage,
    AgentWorkspaceDescriptor<TState>? Workspace,
    AgentStructuredOutputDescriptor<TState>? StructuredOutput = null,
    AgentCheckpointDescriptor<TState>? Checkpoint = null,
    IReadOnlyList<
        Func<PipelineMessage<TState>, CancellationToken, ValueTask<string?>>
    >? MessageAugmentations = null,
    AgentTurnDescriptor<TState>? TurnPolicy = null,
    bool ContinueSession = false,
    Func<TState, AgentProfileSelection>? ProfilePolicy = null,
    Func<PipelineMessage<TState>, BlockOutcome, bool>? RetainConversation = null,
    Func<PipelineMessage<TState>, string>? ContextUserMessage = null,
    AgentImplementationFactory? ImplementationFactory = null,
    TimeSpan? Timeout = null,
    IReadOnlyList<AgentStateGuardDescriptor<TState>>? StateGuards = null,
    IReadOnlyList<AgentLatchedGateDescriptor>? LatchedGates = null,
    IReadOnlyList<AgentSkillDescriptor>? Skills = null
);

internal sealed record AgentWorkspaceDescriptor<TState>(
    Func<TState, string> Path,
    Func<TState, IReadOnlyList<AgentCommandDescriptor>> Commands,
    IReadOnlyList<AgentToolGroupDescriptor<TState>> ToolGroups
);

internal sealed record AgentToolGroupDescriptor<TState>(
    Func<TState, bool> IsAvailable,
    IReadOnlyList<AgentToolSelectionDescriptor> Tools
);

internal enum AgentToolSelectionKind
{
    BuiltIn,
    Commands,
}

internal sealed record AgentToolSelectionDescriptor(
    AgentToolSelectionKind Kind,
    string? Name = null
);

internal sealed record AgentStateGuardDescriptor<TState>(
    string Id,
    Func<TState, bool> IsActive,
    IReadOnlySet<ToolEffect> BlockedEffects,
    string Message,
    string? RemediationCapabilityName
);

internal sealed record AgentLatchedGateDescriptor(
    string Id,
    Func<AgentUsage, bool> Trigger,
    IReadOnlySet<ToolEffect> BlockedEffects,
    string Message,
    string ReleaseCapabilityId,
    string ReleaseCapabilityName,
    bool ResetSessionAfterRelease
);

internal sealed record AgentStructuredOutputDescriptor<TState>(
    Func<string, TState, AgentStructuredOutputResult<TState>> Parse,
    Func<TState, object, TState>? Apply = null,
    Func<
        PipelineMessage<TState>,
        AgentStructuredOutputResult<TState>,
        IReadOnlySet<ToolObservationDescriptor>,
        IReadOnlyList<ToolInvocationObservationDescriptor>,
        string,
        int,
        IReadOnlyList<AgentStructuredOutputProblem>
    >? Accept = null,
    Func<
        PipelineMessage<TState>,
        AgentStructuredOutputResult<TState>,
        IReadOnlySet<ToolObservationDescriptor>,
        IReadOnlyList<ToolInvocationObservationDescriptor>,
        string,
        int,
        CancellationToken,
        ValueTask
    >? AcceptAsync = null,
    string? CorrectionRequiredToolName = null,
    Type? OutputType = null,
    string? ValueType = null,
    string? Instructions = null,
    Func<TState, IReadOnlyList<AgentOutputExampleDescriptor>>? Examples = null
);

internal sealed record AgentOutputExampleDescriptor(string Input, string Output);

internal sealed record AgentTurnDescriptor<TState>(
    int MaxContinuationAttempts,
    Func<
        PipelineMessage<TState>,
        string,
        IReadOnlyList<string>,
        bool,
        int,
        CancellationToken,
        ValueTask<AgentTurnDirectiveDescriptor?>
    > Continue
);

internal sealed record AgentTurnDirectiveDescriptor(string Prompt, string? RequiredToolName);

internal sealed record AgentCheckpointDescriptor<TState>(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent,
    AgentCapabilityDescriptor<TState> Capability,
    string Instructions,
    Func<TState, int, string> UserMessage
)
{
    public int CheckpointAtTokens =>
        (int)Math.Floor(ContextWindowTokens * (CheckpointAtPercent / 100.0));
}

internal sealed record AgentProfileSelection(string ProfileName, string Reason);
