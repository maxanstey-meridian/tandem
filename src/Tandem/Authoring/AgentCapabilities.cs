using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization;
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
}

public sealed class AgentCapability<TState, TRequest> : AgentCapability<TState>
    where TRequest : class
{
    private readonly string _name;
    private readonly string _description;
    private readonly IValidator<TRequest> _validator;
    private readonly Func<TRequest, string> _summarize;
    private readonly Func<TState, TRequest, TState> _apply;

    internal AgentCapability(
        string name,
        string description,
        IValidator<TRequest> validator,
        Func<TRequest, string> summarize,
        Func<TState, TRequest, TState> apply,
        Func<CapabilityAcceptanceContext<TState, TRequest>, CancellationToken, ValueTask>? accept =
            null
    )
        : base(CreateDescriptor(name, description, validator, summarize, apply, accept))
    {
        _name = name;
        _description = description;
        _validator = validator;
        _summarize = summarize;
        _apply = apply;
    }

    internal AgentCapability<TState, TRequest> WithAcceptance(
        Func<CapabilityAcceptanceContext<TState, TRequest>, CancellationToken, ValueTask> accept
    ) => new(_name, _description, _validator, _summarize, _apply, accept);

    private static AgentCapabilityDescriptor<TState> CreateDescriptor(
        string name,
        string description,
        IValidator<TRequest> validator,
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
                summarize,
                apply,
                accept,
                invocation
            )
        );
    }
}

public static class AgentCapabilities
{
    public static AgentCapability<TState, TRequest> Create<TState, TRequest>(
        string name,
        string description,
        IValidator<TRequest> validator,
        Func<TRequest, string> summarize,
        Func<TState, TRequest, TState> apply
    )
        where TRequest : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(summarize);
        ArgumentNullException.ThrowIfNull(apply);
        return new AgentCapability<TState, TRequest>(
            name,
            description,
            validator,
            summarize,
            apply
        );
    }
}

internal sealed record CapabilityAcceptanceContext<TState, TRequest>(
    Guid RunId,
    string BlockId,
    string InvocationId,
    string CapabilityId,
    TState State,
    TRequest Request
);

internal sealed class CapabilityFunction<TState, TRequest> : AIFunction
    where TRequest : class
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerOptions.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string _capabilityId;
    private readonly string _name;
    private readonly string _description;
    private readonly IValidator<TRequest> _validator;
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
        TRequest request;
        try
        {
            request =
                JsonSerializer.Deserialize<TRequest>(
                    JsonSerializer.SerializeToElement(arguments, _jsonOptions),
                    _jsonOptions
                ) ?? throw new JsonException("Request was null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Error($"invalid {_name} call", [ex.Message]);
        }

        var validation = await _validator.ValidateAsync(request, cancellationToken);
        if (!validation.IsValid)
        {
            return Error(
                $"invalid {_name} call",
                validation.Errors.Select(error => error.ErrorMessage)
            );
        }

        string summary;
        JsonElement payload;
        try
        {
            summary = _summarize(request);
            payload = JsonSerializer.SerializeToElement(request, _jsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return Error($"invalid {_name} call", [ex.Message]);
        }

        if (!_invocation.TryReserve())
        {
            return Error("conflicting capability outcome", []);
        }

        try
        {
            var context = new CapabilityAcceptanceContext<TState, TRequest>(
                _invocation.RunId,
                _invocation.BlockId,
                _invocation.InvocationId,
                _capabilityId,
                _invocation.State,
                request
            );
            if (_accept is not null)
            {
                await _accept(context, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var state = _apply(context.State, context.Request);
            _invocation.Commit(
                new AcceptedCapability<TState>(_capabilityId, _name, state, summary, payload)
            );
            return JsonSerializer.SerializeToElement(
                new { accepted = true, outcome = new { kind = _capabilityId, payload } },
                _jsonOptions
            );
        }
        catch (OperationCanceledException)
        {
            _invocation.Release();
            throw;
        }
        catch (Exception ex)
        {
            _invocation.Release();
            return Error("capability acceptance failed", [ex.Message]);
        }
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

    private static JsonElement CreateInputSchema()
    {
        var schema = _jsonOptions.GetJsonSchemaAsNode(typeof(TRequest));
        if (schema is JsonObject root)
        {
            root.Remove("$schema");
            root["type"] = "object";
        }
        RemoveNullableSchemaTypes(schema);
        return JsonSerializer.SerializeToElement(schema, _jsonOptions);
    }

    private static void RemoveNullableSchemaTypes(JsonNode? node)
    {
        if (node is JsonObject value)
        {
            if (value["type"] is JsonArray types)
            {
                value["type"] = types
                    .Select(type => type?.GetValue<string>())
                    .First(type => type != "null");
            }
            foreach (var child in value.ToArray())
            {
                RemoveNullableSchemaTypes(child.Value);
            }
        }
        else if (node is JsonArray values)
        {
            foreach (var child in values.ToArray())
            {
                RemoveNullableSchemaTypes(child);
            }
        }
    }
}
