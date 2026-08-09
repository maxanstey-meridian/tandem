using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem;

public sealed record AgentJsonValidationProblem(
    [property: JsonPropertyName("path")] string Field,
    string Message
);

/// <summary>
/// Defines the adapter-authoring seam for a dynamic structured-output contract.
/// Ordinary C# applications should prefer <see cref="IAgentOutputDefinition{TState,TOutput}"/>.
/// The schema must declare an object root. <see cref="Validate"/> is mandatory and
/// authoritative: Tandem invokes it before contextual validation and never applies an
/// unvalidated value. Tandem does not independently enforce general JSON Schema keywords.
/// This API is intended for adapters that supply schema-backed contracts dynamically;
/// ordinary C# authoring should remain typed.
/// </summary>
public sealed record AgentJsonOutputDefinition<TState>(
    JsonElement JsonSchema,
    string Instructions,
    Func<JsonElement, IReadOnlyList<AgentJsonValidationProblem>> Validate,
    string ValueType,
    Func<TState, JsonElement, IReadOnlyList<AgentJsonValidationProblem>>? ValidateFor = null
);
