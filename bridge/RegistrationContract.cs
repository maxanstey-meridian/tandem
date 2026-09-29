using System.Text.Json.Serialization;

namespace Tandem.NodeApiSpike;

internal sealed record RegisteredGraphContract(
    int ContractVersion,
    string Name,
    string Start,
    string InitialState,
    RegisteredNodeContract[] Nodes,
    RegisteredRouteContract[] Routes,
    string[] Outputs,
    bool Persist = false,
    string? LedgerPath = null,
    RegisteredPresentation? Presentation = null,
    RegisteredTerminalPresentationContract? Terminal = null,
    string? ObservationCallback = null,
    RegisteredInteractionHandlerContract[]? InteractionHandlers = null,
    bool EnableLedgerTools = false
);

internal enum RegisteredPresentation
{
    Terminal,
}

internal sealed record RegisteredTerminalPresentationContract(string[]? TruncatedToolNames = null);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(StageNodeContract), "stage")]
[JsonDerivedType(typeof(InteractionNodeContract), "interaction")]
[JsonDerivedType(typeof(AgentNodeContract), "agent")]
[JsonDerivedType(typeof(ParallelNodeContract), "parallel")]
[JsonDerivedType(typeof(CollectionNodeContract), "collection")]
[JsonDerivedType(typeof(CompletionNodeContract), "completion")]
[JsonDerivedType(typeof(FailureNodeContract), "failure")]
internal abstract record RegisteredNodeContract(string Id, bool? Persist);

internal sealed record StageNodeContract(string Id, string RunCallback, bool? Persist = null)
    : RegisteredNodeContract(Id, Persist);

internal sealed record InteractionNodeContract(
    string Id,
    string RequestCallback,
    string ApplyCallback,
    bool? Persist = null
) : RegisteredNodeContract(Id, Persist);

internal abstract record TerminalNodeContract(string Id, string SummaryCallback, bool? Persist)
    : RegisteredNodeContract(Id, Persist);

internal sealed record CompletionNodeContract(
    string Id,
    string SummaryCallback,
    bool? Persist = null
) : TerminalNodeContract(Id, SummaryCallback, Persist);

internal sealed record FailureNodeContract(string Id, string SummaryCallback, bool? Persist = null)
    : TerminalNodeContract(Id, SummaryCallback, Persist);

internal sealed record AgentNodeContract(
    string Id,
    string Instructions,
    string MessageCallback,
    RegisteredChatClientContract Client,
    RegisteredCapabilityContract[] Capabilities,
    string[] SkillDirectories,
    bool? Persist = null,
    RegisteredAgentOutputContract? Output = null,
    double? Temperature = null,
    int? MaxOutputTokens = null,
    RegisteredReasoningContract? Reasoning = null,
    bool ContinueSession = false,
    double? TimeoutMilliseconds = null,
    RegisteredWorkspaceContract? Workspace = null,
    RegisteredCheckpointContract? Checkpoint = null
) : RegisteredNodeContract(Id, Persist);

internal sealed record ParallelNodeContract(
    string Id,
    RegisteredParallelBranchContract[] Branches,
    string MergeCallback,
    bool? Persist = null,
    int? Max = null
) : RegisteredNodeContract(Id, Persist);

internal sealed record CollectionNodeContract(
    string Id,
    int Max,
    string ItemsCallback,
    string RunCallback,
    string ApplyCallback,
    RegisteredNodeContract[] Agents,
    bool? Persist = null
) : RegisteredNodeContract(Id, Persist);

internal sealed record RegisteredParallelBranchContract(
    string Id,
    RegisteredNodeContract Participant
);

internal sealed record RegisteredReasoningContract(
    AgentReasoningEffort? Effort = null,
    int? MaxTokens = null
);

internal sealed record RegisteredCheckpointContract(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent,
    string CapabilityName,
    string Instructions,
    string MessageCallback,
    bool ResetSession = false,
    bool DisableCompaction = false
);

internal sealed record RegisteredWorkspaceContract(
    string PathCallback,
    string CommandsCallback,
    RegisteredToolGroupContract[] ToolGroups,
    string? InterceptCallback = null
);

internal sealed record RegisteredToolGroupContract(
    string[] Tools,
    bool IncludeCommands = false,
    string? WhenCallback = null
);

internal enum RegisteredChatClientKind
{
    [JsonStringEnumMemberName("openai-compatible")]
    OpenAiCompatible,
}

internal enum RegisteredWireApi
{
    Completions,
    Responses,
}

internal sealed record RegisteredChatClientContract(
    RegisteredChatClientKind Kind,
    int Version,
    string Endpoint,
    string Model,
    RegisteredWireApi WireApi,
    string? ApiKeyEnvironmentVariable = null,
    bool VerifyModel = false,
    int? RequestTimeoutMs = null,
    int? IdleTimeoutMs = null,
    int? MaxAttempts = null
);

internal sealed record RegisteredAgentOutputContract(
    string Instructions,
    string ApplyCallback,
    string ValueType,
    string? JsonSchema = null,
    string? ValidateCallback = null,
    string? ValidateForCallback = null,
    bool Raw = false,
    string? RawParseCallback = null
);

internal sealed record RegisteredCapabilityContract(
    string Name,
    string Instructions,
    string JsonSchema,
    string ValidateCallback,
    string ApplyCallback,
    string SummaryCallback,
    string ValueType,
    string? ValidateForCallback = null
);

internal enum RegisteredRouteOutcome
{
    Success,
    Failed,
}

internal sealed record RegisteredRouteContract(
    string Source,
    string Target,
    string Label,
    string? PredicateCallback = null,
    RegisteredRouteOutcome? Outcome = null
);

internal sealed record RegisteredInteractionHandlerContract(
    string Id,
    string Target,
    string HandleCallback
);
