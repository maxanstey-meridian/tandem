using System.Text.Json;
using Tandem.Infrastructure;

namespace Tandem;

/// <summary>
/// Defines the adapter-authoring seam for a dynamic capability contract.
/// Ordinary C# applications should prefer typed capabilities. The schema must declare an
/// object root. <see cref="Validate"/> is mandatory and authoritative, runs before
/// <see cref="ValidateFor"/>, and both complete before summary, acceptance, or application.
/// Tandem does not independently enforce general JSON Schema keywords.
/// This API is intended for adapters that supply schema-backed contracts dynamically;
/// ordinary C# authoring should remain typed.
/// </summary>
public sealed record AgentJsonCapabilityDefinition<TState>(
    string ToolName,
    string Instructions,
    JsonElement JsonSchema,
    Func<JsonElement, IReadOnlyList<AgentJsonValidationProblem>> Validate,
    Func<TState, JsonElement, IReadOnlyList<AgentJsonValidationProblem>>? ValidateFor,
    Func<JsonElement, string> Summarize,
    string ValueType
);

public static partial class AgentCapabilities
{
    public static AgentCapability<TState> CreateJson<TState>(
        AgentJsonCapabilityDefinition<TState> capability,
        Func<TState, JsonElement, TState> apply
    )
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.ToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.Instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.ValueType);
        ArgumentNullException.ThrowIfNull(capability.Validate);
        ArgumentNullException.ThrowIfNull(capability.Summarize);
        ArgumentNullException.ThrowIfNull(apply);
        var contract = new CapabilityContract<TState, JsonElement>(
            CapabilityContract.IdFor<TState>(capability.ToolName),
            capability.ToolName,
            capability.Instructions,
            CapabilityContract.RequireObjectRoot(
                capability.JsonSchema,
                "Capability",
                nameof(capability)
            ),
            capability.ValueType,
            (request, state, _) =>
            {
                var problems = capability.Validate(request);
                if (problems.Count == 0 && capability.ValidateFor is { } validateFor)
                {
                    problems = validateFor(state, request);
                }
                return ValueTask.FromResult(
                    new CapabilityRequest<JsonElement>(
                        request,
                        [
                            .. problems.Select(problem => new ToolProblem(
                                problem.Field,
                                problem.Message
                            )),
                        ]
                    )
                );
            },
            capability.Summarize,
            apply,
            ObserveTypedRequest: false
        );
        return new AgentCapability<TState>(CreateDescriptor(contract, null));

        static AgentCapabilityDescriptor<TState> CreateDescriptor(
            CapabilityContract<TState, JsonElement> contract,
            Func<
                CapabilityAcceptanceContext<TState, JsonElement>,
                CancellationToken,
                ValueTask
            >? accept
        ) =>
            new(
                contract.CapabilityId,
                contract.Name,
                invocation => new CapabilityFunction<TState, JsonElement>(
                    contract,
                    accept,
                    invocation
                ),
                nextAccept => CreateDescriptor(contract, nextAccept)
            );
    }
}
