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
    CheckpointPolicy? Checkpoint = null
);

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
public delegate StructuredOutcome StructuredOutputParser(
    string assistantText,
    PipelineContext context
);

public sealed record StructuredOutcome(
    string Kind,
    string Summary,
    System.Text.Json.JsonElement Payload,
    PipelineContext? UpdatedContext = null
);
