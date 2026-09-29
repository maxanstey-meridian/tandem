using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

namespace Tandem;

public class AgentCapability<TState>
{
    internal AgentCapability(AgentCapabilityDescriptor<TState> descriptor)
    {
        Descriptor = descriptor;
    }

    internal AgentCapabilityDescriptor<TState> Descriptor { get; }
    internal string ToolName => Descriptor.ToolName;

    internal AIFunction Bind(CapabilityInvocationState<TState> invocation) =>
        Descriptor.Bind(invocation);

    internal AgentCapability<TState> WithJsonAcceptance(
        Func<CapabilityAcceptanceContext<TState, JsonElement>, CancellationToken, ValueTask> accept
    ) =>
        new(
            (
                Descriptor.WithJsonAcceptance
                ?? throw new InvalidOperationException(
                    "Only dynamic JSON capabilities support representation-neutral acceptance."
                )
            )(accept)
        );
}

public sealed class AgentCapability<TState, TRequest> : AgentCapability<TState>
    where TRequest : class
{
    private readonly string _name;
    private readonly string _description;
    private readonly IValidator<TRequest> _validator;
    private readonly Func<TState, IValidator<TRequest>?> _contextualValidator;
    private readonly Func<TRequest, string> _summarize;
    private readonly Func<TState, TRequest, TState> _apply;

    internal AgentCapability(
        string name,
        string description,
        IValidator<TRequest> validator,
        Func<TState, IValidator<TRequest>?> contextualValidator,
        Func<TRequest, string> summarize,
        Func<TState, TRequest, TState> apply,
        Func<CapabilityAcceptanceContext<TState, TRequest>, CancellationToken, ValueTask>? accept =
            null
    )
        : base(
            CreateDescriptor(
                name,
                description,
                validator,
                contextualValidator,
                summarize,
                apply,
                accept
            )
        )
    {
        _name = name;
        _description = description;
        _validator = validator;
        _contextualValidator = contextualValidator;
        _summarize = summarize;
        _apply = apply;
    }

    internal AgentCapability<TState, TRequest> WithAcceptance(
        Func<CapabilityAcceptanceContext<TState, TRequest>, CancellationToken, ValueTask> accept
    ) => new(_name, _description, _validator, _contextualValidator, _summarize, _apply, accept);

    private static AgentCapabilityDescriptor<TState> CreateDescriptor(
        string name,
        string description,
        IValidator<TRequest> validator,
        Func<TState, IValidator<TRequest>?> contextualValidator,
        Func<TRequest, string> summarize,
        Func<TState, TRequest, TState> apply,
        Func<CapabilityAcceptanceContext<TState, TRequest>, CancellationToken, ValueTask>? accept
    )
    {
        var contract = new CapabilityContract<TState, TRequest>(
            CapabilityContract.IdFor<TState>(name),
            name,
            description,
            AIJsonUtilities.CreateJsonSchema(
                typeof(TRequest),
                serializerOptions: TandemJson.TypedContract
            ),
            typeof(TRequest).FullName ?? typeof(TRequest).Name,
            async (payload, state, cancellationToken) =>
            {
                TRequest request;
                try
                {
                    request =
                        payload.Deserialize<TRequest>(TandemJson.TypedContract)
                        ?? throw new JsonException("Request was null.");
                }
                catch (Exception ex) when (ex is JsonException or NotSupportedException)
                {
                    return new(
                        null,
                        [
                            new ToolProblem(null, ex.Message),
                            new ToolProblem(null, $"Received arguments: {payload.GetRawText()}"),
                        ]
                    );
                }

                var problems = ToolProblem.From(
                    await validator.ValidateAsync(request, cancellationToken)
                );
                if (problems.Count == 0 && contextualValidator(state) is { } contextual)
                {
                    problems = ToolProblem.From(
                        await contextual.ValidateAsync(request, cancellationToken)
                    );
                }
                return new(request, problems);
            },
            summarize,
            apply,
            ObserveTypedRequest: true
        );
        return new AgentCapabilityDescriptor<TState>(
            contract.CapabilityId,
            name,
            invocation => new CapabilityFunction<TState, TRequest>(contract, accept, invocation)
        );
    }
}

public static partial class AgentCapabilities
{
    public static AgentCapability<TState, TRequest> Create<TState, TRequest>(
        IAgentCapabilityDefinition<TState, TRequest> capability,
        Func<TState, TRequest, TState> apply
    )
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.ToolName);
        ArgumentException.ThrowIfNullOrWhiteSpace(capability.Instructions);
        ArgumentNullException.ThrowIfNull(capability.Validator);
        ArgumentNullException.ThrowIfNull(apply);
        return new AgentCapability<TState, TRequest>(
            capability.ToolName,
            capability.Instructions,
            capability.Validator,
            capability.ValidatorFor,
            capability.Summarize,
            apply
        );
    }
}

internal sealed record CapabilityAcceptanceContext<TState, TRequest>(
    Guid RunId,
    string StepId,
    string InvocationId,
    string CapabilityId,
    TState State,
    TRequest Request
)
{
    internal IReadOnlyList<ToolInvocationObservationDescriptor> ToolInvocations { get; init; } = [];
    internal string AcceptedCallId => $"{RunId:N}:{StepId}:{InvocationId}:{CapabilityId}";
}

internal static class CapabilityContract
{
    public static string IdFor<TState>(string toolName) =>
        $"capability:{typeof(TState).FullName}:{toolName}";

    public static JsonElement RequireObjectRoot(
        JsonElement schema,
        string contract,
        string parameter
    )
    {
        if (
            schema.ValueKind is not JsonValueKind.Object
            || !schema.TryGetProperty("type", out var rootType)
            || rootType.ValueKind is not JsonValueKind.String
            || rootType.GetString() != "object"
        )
        {
            throw new ArgumentException(
                $"{contract} JSON schema must declare an object root with type 'object'.",
                parameter
            );
        }
        return schema.Clone();
    }
}

internal sealed record CapabilityRequest<TRequest>(
    TRequest? Request,
    IReadOnlyList<ToolProblem> Problems
);

/// <summary>
/// One capability contract for typed and JSON capabilities: parse (deserialise and validate,
/// or validate raw JSON), summarise, then accept and apply.
/// </summary>
internal sealed record CapabilityContract<TState, TRequest>(
    string CapabilityId,
    string Name,
    string Description,
    JsonElement Schema,
    string RequestType,
    Func<JsonElement, TState, CancellationToken, ValueTask<CapabilityRequest<TRequest>>> Parse,
    Func<TRequest, string> Summarize,
    Func<TState, TRequest, TState> Apply,
    bool ObserveTypedRequest
);

internal sealed class CapabilityFunction<TState, TRequest>(
    CapabilityContract<TState, TRequest> contract,
    Func<CapabilityAcceptanceContext<TState, TRequest>, CancellationToken, ValueTask>? accept,
    CapabilityInvocationState<TState> invocation
) : AIFunction
{
    public override string Name => contract.Name;
    public override string Description => contract.Description;
    public override JsonElement JsonSchema => contract.Schema;
    public override JsonSerializerOptions JsonSerializerOptions => TandemJson.TypedContract;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(arguments, TandemJson.TypedContract);
        var parsed = await contract.Parse(payload, invocation.State, cancellationToken);
        if (parsed.Problems.Count > 0)
        {
            return ToolError.InvalidCall(contract.Name, parsed.Problems);
        }
        var request = parsed.Request!;

        string summary;
        try
        {
            summary = contract.Summarize(request);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return ToolError.InvalidCall(contract.Name, [new ToolProblem(null, ex.Message)]);
        }

        var context = new CapabilityAcceptanceContext<TState, TRequest>(
            invocation.RunId,
            invocation.StepId,
            invocation.InvocationId,
            contract.CapabilityId,
            invocation.State,
            request
        )
        {
            ToolInvocations = invocation.ToolInvocations,
        };
        return await CapabilityAcceptanceRuntime.AcceptAsync(
            invocation,
            new PipelineCapabilityAccepted(
                invocation.RunId,
                invocation.StepId,
                invocation.InvocationId,
                contract.CapabilityId,
                contract.Name,
                context.AcceptedCallId,
                summary,
                contract.RequestType,
                payload
            ),
            accepted =>
                contract.ObserveTypedRequest
                    ? new CapabilityAccepted<TRequest>(
                        accepted.RunId,
                        accepted.StepId,
                        accepted.InvocationId,
                        accepted.CapabilityId,
                        accepted.CapabilityName,
                        accepted.AcceptedCallId,
                        accepted.Summary,
                        accepted.RequestType,
                        accepted.Payload,
                        request
                    )
                    : accepted,
            accept is null ? null : ct => accept(context, ct),
            state => contract.Apply(state, request),
            cancellationToken
        );
    }
}
