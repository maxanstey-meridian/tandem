using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.AI;

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
        var capabilityId = $"capability:{typeof(TState).FullName}:{name}";
        return new AgentCapabilityDescriptor<TState>(
            capabilityId,
            name,
            invocation => new CapabilityFunction<TState, TRequest>(
                capabilityId,
                name,
                description,
                validator,
                contextualValidator,
                summarize,
                apply,
                accept,
                invocation
            )
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
    internal IReadOnlyList<Infrastructure.ToolInvocationObservationDescriptor> ToolInvocations { get; init; } =
    [];
    internal string AcceptedCallId => $"{RunId:N}:{StepId}:{InvocationId}:{CapabilityId}";
}

internal sealed class CapabilityFunction<TState, TRequest> : AIFunction
    where TRequest : class
{
    private static readonly JsonSerializerOptions _jsonOptions = TandemJson.TypedContract;
    private readonly string _capabilityId;
    private readonly string _name;
    private readonly string _description;
    private readonly IValidator<TRequest> _validator;
    private readonly Func<TState, IValidator<TRequest>?> _contextualValidator;
    private readonly Func<TRequest, string> _summarize;
    private readonly Func<TState, TRequest, TState> _apply;
    private readonly Func<
        CapabilityAcceptanceContext<TState, TRequest>,
        CancellationToken,
        ValueTask
    >? _accept;
    private readonly CapabilityInvocationState<TState> _invocation;
    private readonly JsonElement _schema;

    internal CapabilityFunction(
        string capabilityId,
        string name,
        string description,
        IValidator<TRequest> validator,
        Func<TState, IValidator<TRequest>?> contextualValidator,
        Func<TRequest, string> summarize,
        Func<TState, TRequest, TState> apply,
        Func<CapabilityAcceptanceContext<TState, TRequest>, CancellationToken, ValueTask>? accept,
        CapabilityInvocationState<TState> invocation
    )
    {
        _capabilityId = capabilityId;
        _name = name;
        _description = description;
        _validator = validator;
        _contextualValidator = contextualValidator;
        _summarize = summarize;
        _apply = apply;
        _accept = accept;
        _invocation = invocation;
        _schema = CreateInputSchema();
    }

    public override string Name => _name;
    public override string Description => _description;
    public override JsonElement JsonSchema => _schema;
    public override JsonSerializerOptions JsonSerializerOptions => _jsonOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(arguments, _jsonOptions);
        TRequest request;
        try
        {
            request =
                payload.Deserialize<TRequest>(_jsonOptions)
                ?? throw new JsonException("Request was null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Error(
                $"invalid {_name} call",
                [ex.Message, $"Received arguments: {payload.GetRawText()}"]
            );
        }

        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Error(
                $"invalid {_name} call",
                validation.Errors.Select(error => error.ErrorMessage)
            );
        }
        if (_contextualValidator(_invocation.State) is { } contextualValidator)
        {
            validation = await contextualValidator.ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Error(
                    $"invalid {_name} call",
                    validation.Errors.Select(error => error.ErrorMessage)
                );
            }
        }

        string summary;
        try
        {
            summary = _summarize(request);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Error($"invalid {_name} call", [ex.Message]);
        }

        var context = new CapabilityAcceptanceContext<TState, TRequest>(
            _invocation.RunId,
            _invocation.StepId,
            _invocation.InvocationId,
            _capabilityId,
            _invocation.State,
            request
        )
        {
            ToolInvocations = _invocation.ToolInvocations,
        };
        return await CapabilityAcceptanceRuntime.AcceptAsync(
            _invocation,
            _capabilityId,
            _name,
            typeof(TRequest).FullName ?? typeof(TRequest).Name,
            payload,
            summary,
            acceptedPayload => new CapabilityAccepted<TRequest>(
                _invocation.RunId,
                _invocation.StepId,
                _invocation.InvocationId,
                _capabilityId,
                _name,
                $"{_invocation.RunId:N}:{_invocation.StepId}:{_invocation.InvocationId}:{_capabilityId}",
                summary,
                typeof(TRequest).FullName ?? typeof(TRequest).Name,
                acceptedPayload,
                request
            ),
            _accept is null ? null : ct => _accept(context, ct),
            state => _apply(state, request),
            cancellationToken
        );
    }

    private static JsonElement Error(string error, IEnumerable<string> problems) =>
        JsonSerializer.SerializeToElement(
            new
            {
                isError = true,
                error,
                problems = problems.ToArray(),
            },
            _jsonOptions
        );

    private static JsonElement CreateInputSchema() =>
        AIJsonUtilities.CreateJsonSchema(
            typeof(TRequest),
            description: null,
            hasDefaultValue: false,
            defaultValue: null,
            _jsonOptions
        );
}
