using Tandem.Infrastructure;

namespace Tandem.Domain;

internal sealed record AgentBlockConfig<TState>(
    string BlockId,
    string ProfileName,
    string SystemInstructions,
    IReadOnlyList<AgentCapabilityDescriptor<TState>> Capabilities,
    Func<TState, string>? UserMessage,
    Func<TState, string>? WorkspacePath,
    Func<TState, bool>? AllowMutation,
    AgentStructuredOutputDescriptor<TState>? StructuredOutput = null,
    AgentCheckpointDescriptor<TState>? Checkpoint = null,
    Func<PipelineMessage<TState>, CancellationToken, ValueTask<string?>>? MessageAugmentation =
        null,
    AgentTurnDescriptor<TState>? TurnPolicy = null,
    bool ContinueSession = false,
    Func<TState, AgentProfileSelection>? ProfilePolicy = null,
    Func<PipelineMessage<TState>, BlockOutcome, bool>? RetainConversation = null,
    Func<PipelineMessage<TState>, string>? ContextUserMessage = null,
    AgentImplementationFactory? ImplementationFactory = null,
    TimeSpan? Timeout = null,
    IReadOnlyList<AgentStateGuardDescriptor<TState>>? StateGuards = null,
    IReadOnlyList<AgentLatchedGateDescriptor>? LatchedGates = null
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
    Func<
        PipelineMessage<TState>,
        AgentStructuredOutputResult<TState>,
        IReadOnlySet<ToolObservationDescriptor>,
        string,
        int,
        IReadOnlyList<AgentStructuredOutputProblem>
    >? Accept = null,
    Func<
        PipelineMessage<TState>,
        AgentStructuredOutputResult<TState>,
        IReadOnlySet<ToolObservationDescriptor>,
        string,
        int,
        CancellationToken,
        ValueTask
    >? AcceptAsync = null,
    string? CorrectionRequiredToolName = null,
    Type? OutputType = null
);

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
