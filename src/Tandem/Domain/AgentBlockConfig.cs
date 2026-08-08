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
    TimeSpan? Timeout = null
);

internal sealed record AgentStructuredOutputDescriptor<TState>(
    Func<string, TState, AgentStructuredOutputResult<TState>> Parse,
    Func<
        PipelineMessage<TState>,
        AgentStructuredOutputResult<TState>,
        IReadOnlySet<ToolObservationDescriptor>,
        int,
        IReadOnlyList<AgentStructuredOutputProblem>
    >? Accept = null,
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
