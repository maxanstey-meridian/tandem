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
    string? LedgerPath
);

internal sealed record RegisteredNodeContract(
    string? Id,
    string? Kind,
    string? Instructions,
    string? RunCallback,
    string? RequestCallback,
    string? HandleCallback,
    string? ApplyCallback,
    string? SummaryCallback,
    string? MessageCallback,
    RegisteredChatClientContract? Client,
    RegisteredAgentOutputContract? Output,
    RegisteredCapabilityContract[]? Capabilities,
    bool ContinueSession,
    double? TimeoutMilliseconds,
    bool? Persist
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
    string? JsonSchema,
    string? ValidateCallback,
    string? ApplyCallback,
    string? ValueType
);

internal sealed record RegisteredCapabilityContract(
    string? Name,
    string? JsonSchema,
    string? ValidateCallback,
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
