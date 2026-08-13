using System.Text.Json;
using Microsoft.Extensions.AI;

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
        if (
            capability.JsonSchema.ValueKind is not JsonValueKind.Object
            || !capability.JsonSchema.TryGetProperty("type", out var rootType)
            || rootType.ValueKind is not JsonValueKind.String
            || rootType.GetString() != "object"
        )
        {
            throw new ArgumentException(
                "Capability JSON schema must declare an object root with type 'object'.",
                nameof(capability)
            );
        }
        capability = capability with { JsonSchema = capability.JsonSchema.Clone() };

        var capabilityId = $"capability:{typeof(TState).FullName}:{capability.ToolName}";
        return new AgentCapability<TState>(CreateDescriptor(capabilityId, capability, apply, null));

        static AgentCapabilityDescriptor<TState> CreateDescriptor(
            string capabilityId,
            AgentJsonCapabilityDefinition<TState> capability,
            Func<TState, JsonElement, TState> apply,
            Func<
                CapabilityAcceptanceContext<TState, JsonElement>,
                CancellationToken,
                ValueTask
            >? accept
        ) =>
            new(
                capabilityId,
                capability.ToolName,
                invocation => new JsonCapabilityFunction<TState>(
                    capabilityId,
                    capability,
                    apply,
                    accept,
                    invocation
                ),
                nextAccept => CreateDescriptor(capabilityId, capability, apply, nextAccept)
            );
    }
}

internal sealed class JsonCapabilityFunction<TState>(
    string capabilityId,
    AgentJsonCapabilityDefinition<TState> definition,
    Func<TState, JsonElement, TState> apply,
    Func<CapabilityAcceptanceContext<TState, JsonElement>, CancellationToken, ValueTask>? accept,
    CapabilityInvocationState<TState> invocation
) : AIFunction
{
    private static readonly JsonSerializerOptions _jsonOptions = TandemJson.TypedContract;

    public override string Name => definition.ToolName;
    public override string Description => definition.Instructions;
    public override JsonElement JsonSchema => definition.JsonSchema;
    public override JsonSerializerOptions JsonSerializerOptions => _jsonOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        var request = JsonSerializer.SerializeToElement(arguments, _jsonOptions);
        var problems = definition.Validate(request).ToArray();
        if (problems.Length > 0)
        {
            return ValidationError($"invalid {definition.ToolName} call", problems);
        }
        problems = definition.ValidateFor?.Invoke(invocation.State, request).ToArray() ?? [];
        if (problems.Length > 0)
        {
            return ValidationError($"invalid {definition.ToolName} call", problems);
        }

        var summary = definition.Summarize(request);
        var context = new CapabilityAcceptanceContext<TState, JsonElement>(
            invocation.RunId,
            invocation.StepId,
            invocation.InvocationId,
            capabilityId,
            invocation.State,
            request
        );
        return await CapabilityAcceptanceRuntime.AcceptAsync(
            invocation,
            capabilityId,
            definition.ToolName,
            definition.ValueType,
            request,
            summary,
            emitAccepted: null,
            accept is null ? null : ct => accept(context, ct),
            state => apply(state, request),
            cancellationToken
        );
    }

    private static JsonElement ValidationError(
        string error,
        IEnumerable<AgentJsonValidationProblem> problems
    ) =>
        JsonSerializer.SerializeToElement(
            new
            {
                isError = true,
                error,
                problems = problems.Select(problem => new
                {
                    field = problem.Field,
                    message = problem.Message,
                }),
            },
            _jsonOptions
        );
}
