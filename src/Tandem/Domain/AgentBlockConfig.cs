namespace Tandem.Domain;

public enum WorkspaceAccess
{
    ReadOnly,
    MutationGated,
}

public sealed record AgentBlockConfig(
    string BlockId,
    string ProfileName,
    string SystemInstructions,
    WorkspaceAccess Access,
    IReadOnlyList<string> LifecycleToolNames,
    StructuredOutputParser? StructuredOutput = null,
    CheckpointPolicy? Checkpoint = null,
    MessageAugmentation? MessageAugmentation = null,
    AgentTurnPolicy? TurnPolicy = null
);

/// <summary>
/// Produces additional prompt text from the pipeline context before the model
/// is invoked. Returns null to add nothing.
/// </summary>
public delegate ValueTask<string?> MessageAugmentation(
    PipelineContext context,
    CancellationToken cancellationToken
);

public sealed record AgentTurnPolicy
{
    public AgentTurnPolicy(int maxContinuationAttempts, AgentTurnContinuationPolicy @continue)
    {
        if (maxContinuationAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContinuationAttempts));
        }

        MaxContinuationAttempts = maxContinuationAttempts;
        Continue = @continue;
    }

    public int MaxContinuationAttempts { get; }
    public AgentTurnContinuationPolicy Continue { get; }
}

public sealed record AgentTurnObservation(
    PipelineContext Context,
    string AssistantText,
    IReadOnlyList<string> ToolNames,
    bool HasAcceptedLifecycleOutcome,
    int ContinuationAttempt
);

public sealed record AgentTurnDirective(string Prompt, string? RequiredToolName = null);

public delegate ValueTask<AgentTurnDirective?> AgentTurnContinuationPolicy(
    AgentTurnObservation observation,
    CancellationToken cancellationToken
);

/// <summary>
/// Result of a tool interception check. When blocked, the supplied message
/// is returned to the model as the tool result instead of executing the call.
/// </summary>
public abstract record ToolInterceptionResult
{
    public sealed record Blocked(string Message) : ToolInterceptionResult;
}

/// <summary>
/// Threshold values for checkpoint-only mode. When the executor's context
/// tokens plus max output tokens reach the checkpoint threshold, the block
/// runs in checkpoint-only mode.
/// </summary>
public sealed record CheckpointPolicy(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent
)
{
    public int CheckpointAtTokens =>
        (int)Math.Floor(ContextWindowTokens * (CheckpointAtPercent / 100.0));
}

/// <summary>
/// Parses assistant text as JSON and maps it to a BlockOutcome.
/// Used by planner and reviewer blocks that return structured decisions
/// instead of calling lifecycle MCP tools.
/// </summary>
public delegate StructuredOutputResult StructuredOutputParser(
    string assistantText,
    PipelineContext context
);

public sealed record StructuredOutcome(
    string Kind,
    string Summary,
    System.Text.Json.JsonElement Payload,
    PipelineContext? UpdatedContext = null
);
