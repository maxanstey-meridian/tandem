using System.Text.Json;

namespace Tandem.Domain;

internal sealed record AgentBlockConfig<TState>(
    string BlockId,
    string ProfileName,
    string SystemInstructions,
    IReadOnlyList<string> LifecycleActionNames,
    Func<TState, string>? UserMessage,
    Func<TState, string>? WorkspacePath,
    Func<TState, bool>? AllowMutation,
    StructuredOutputParser<TState>? StructuredOutput = null,
    CheckpointPolicy<TState>? Checkpoint = null,
    MessageAugmentation<TState>? MessageAugmentation = null,
    AgentTurnPolicy<TState>? TurnPolicy = null,
    StructuredOutputAcceptancePolicy<TState>? StructuredOutputAcceptance = null,
    string? StructuredOutputCorrectionRequiredToolName = null,
    ReceiptStateTransition<TState>? ReceiptTransition = null,
    string? LifecycleActionSetIdentity = null,
    AgentSessionPolicy<TState>? SessionPolicy = null,
    AgentProfilePolicy<TState>? ProfilePolicy = null,
    AgentConversationPolicy<TState>? ConversationPolicy = null,
    AdvancedAgentMessage<TState>? ContextUserMessage = null
);

public enum AgentSessionAction
{
    Continue,
    Reset,
    Teardown,
}

public sealed record AgentSessionDecision(AgentSessionAction Action, string Reason);

public sealed record AgentProfileDecision(string ProfileName, string Reason);

public enum AgentConversationRetention
{
    Retain,
    Discard,
}

public sealed record AgentConversationDecision(AgentConversationRetention Retention, string Reason);

public delegate AgentSessionDecision AgentSessionPolicy<TState>(TState state);

public delegate AgentProfileDecision AgentProfilePolicy<TState>(TState state);

public delegate string AdvancedAgentMessage<TState>(PipelineMessage<TState> message);

public delegate AgentConversationDecision AgentConversationPolicy<TState>(
    PipelineMessage<TState> message,
    BlockOutcome outcome
);

public delegate ValueTask<string?> MessageAugmentation<TState>(
    PipelineMessage<TState> message,
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

public sealed record AgentTurnObservation<TState>(
    PipelineMessage<TState> Message,
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

public abstract record ToolInterceptionResult
{
    public sealed record Blocked(string Message) : ToolInterceptionResult;
}

public sealed record ToolInvocation(string Name);

public sealed record CheckpointPolicy<TState>(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent,
    AgentCapability<TState> Capability,
    string Instructions,
    Func<PipelineMessage<TState>, string> UserMessage
)
{
    public int CheckpointAtTokens =>
        (int)Math.Floor(ContextWindowTokens * (CheckpointAtPercent / 100.0));
}

public delegate StructuredOutputResult<TState> StructuredOutputParser<TState>(
    string assistantText,
    TState state
);

public sealed record StructuredOutputAcceptanceObservation<TState>(
    PipelineMessage<TState> Message,
    StructuredOutputResult<TState> Result,
    IReadOnlySet<string> ToolNames,
    int Attempt
);

public delegate IReadOnlyList<StructuredOutputProblem> StructuredOutputAcceptancePolicy<TState>(
    StructuredOutputAcceptanceObservation<TState> observation
);

public sealed record StructuredOutcome<TState>(
    string Kind,
    string Summary,
    JsonElement Payload,
    TState? UpdatedState = default
);

public delegate TState ReceiptStateTransition<TState>(
    TState state,
    string kind,
    JsonElement payload
);

public delegate ValueTask<ToolInterceptionResult?> ToolInterceptor<TState>(
    PipelineMessage<TState> message,
    ToolInvocation invocation,
    CancellationToken cancellationToken
);
