namespace Tandem.NodeApiSpike;

internal sealed record RegisteredGraphContract(
    int ContractVersion,
    string? Name,
    string? Start,
    string? InitialState,
    bool Persist,
    RegisteredNodeContract[]? Nodes,
    RegisteredRouteContract[]? Routes,
    string[]? Outputs,
    string? LedgerPath,
    string? Presentation,
    string? ObservationCallback,
    RegisteredInteractionHandlerContract[]? InteractionHandlers
);

internal sealed record RegisteredNodeContract(
    string? Id,
    string? Kind,
    string? Instructions,
    string? RunCallback,
    string? RequestCallback,
    string? ApplyCallback,
    string? SummaryCallback,
    string? MessageCallback,
    RegisteredChatClientContract? Client,
    RegisteredAgentOutputContract? Output,
    RegisteredCapabilityContract[]? Capabilities,
    string[]? SkillDirectories,
    double? Temperature,
    int? MaxOutputTokens,
    bool ContinueSession,
    double? TimeoutMilliseconds,
    bool? Persist,
    RegisteredParallelBranchContract[]? Branches = null,
    string? MergeCallback = null,
    RegisteredWorkspaceContract? Workspace = null
);

internal sealed record RegisteredWorkspaceContract(
    string? PathCallback,
    string? CommandsCallback,
    RegisteredToolGroupContract[]? ToolGroups
);

internal sealed record RegisteredToolGroupContract(
    string[]? Tools,
    bool IncludeCommands,
    string? WhenCallback
);

internal sealed record RegisteredParallelBranchContract(
    string? Id,
    RegisteredNodeContract? Participant
);

internal sealed record RegisteredChatClientContract(
    string? Kind,
    int Version,
    string? Endpoint,
    string? Model,
    string? WireApi,
    string? ApiKeyEnvironmentVariable,
    string? ReasoningEffort,
    bool VerifyModel
);

internal sealed record RegisteredAgentOutputContract(
    string? Instructions,
    string? JsonSchema,
    string? ValidateCallback,
    string? ValidateForCallback,
    string? ApplyCallback,
    string? ValueType
);

internal sealed record RegisteredCapabilityContract(
    string? Name,
    string? Instructions,
    string? JsonSchema,
    string? ValidateCallback,
    string? ValidateForCallback,
    string? ApplyCallback,
    string? SummaryCallback,
    string? ValueType
);

internal sealed record RegisteredRouteContract(
    string? Source,
    string? Target,
    string? Label,
    string? PredicateCallback,
    string? Outcome
);

internal sealed record RegisteredInteractionHandlerContract(
    string? Id,
    string? Target,
    string? HandleCallback
);
