using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tandem.Actions;
using Tandem.Domain;

namespace Tandem;

internal interface IAgentCapabilityRegistration
{
    public LifecycleActionSetRegistration Registration { get; }
    public string OwnerIdentity { get; }
    public string ToolName { get; }
}

public sealed class AgentCapability<TState> : IAgentCapabilityRegistration
{
    internal AgentCapability(
        string identity,
        string toolName,
        string receiptKind,
        McpServerTool tool,
        ReceiptStateTransition<TState> transition
    )
    {
        Identity = identity;
        ToolName = toolName;
        ReceiptKind = receiptKind;
        Tool = tool;
        Transition = transition;
    }

    internal string Identity { get; }
    internal string ToolName { get; }
    internal string ReceiptKind { get; }
    internal McpServerTool Tool { get; }
    internal ReceiptStateTransition<TState> Transition { get; }
    string IAgentCapabilityRegistration.OwnerIdentity =>
        typeof(TState).FullName ?? typeof(TState).Name;
    string IAgentCapabilityRegistration.ToolName => ToolName;
    LifecycleActionSetRegistration IAgentCapabilityRegistration.Registration =>
        new(Identity, services => services.AddMcpServer().WithTools([Tool]));
}

public static class AgentCapabilities
{
    public static AgentCapability<TState> Create<TState, TRequest>(
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
        var stateName = typeof(TState).Name;
        var identity = stateName.EndsWith("State", StringComparison.Ordinal)
            ? stateName[..^"State".Length].ToLowerInvariant()
            : stateName.ToLowerInvariant();
        var receiptKind = $"capability:{typeof(TState).FullName}:{name}";

        async ValueTask<CallToolResult> InvokeAsync(
            TRequest request,
            IServiceProvider services,
            CancellationToken cancellationToken
        )
        {
            var validation = await validator.ValidateAsync(request, cancellationToken);
            if (!validation.IsValid)
            {
                return Result(
                    new
                    {
                        error = $"invalid {name} call",
                        problems = validation.Errors.Select(failure => failure.ErrorMessage),
                    },
                    isError: true
                );
            }

            var summary = summarize(request);
            var payload = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
            var context = services.GetRequiredService<LifecycleToolContext>();
            var receipts = services.GetRequiredService<LifecycleReceiptStore>();
            var accepted = await receipts.CreateOrReadAsync(
                context.RunId,
                context.InvocationId,
                context.BlockId,
                receiptKind,
                summary,
                payload,
                cancellationToken
            );
            var receipt = accepted.Receipt;
            if (
                receipt.Kind != receiptKind
                || receipt.Summary != summary
                || !JsonElement.DeepEquals(receipt.Payload, payload)
            )
            {
                return Result(new { error = "conflicting capability outcome" }, isError: true);
            }

            return Result(
                new
                {
                    accepted = true,
                    outcome = new { kind = receipt.Kind, payload = receipt.Payload },
                },
                isError: false
            );
        }

        var tool = new CapabilityMcpTool<TRequest>(name, description, InvokeAsync);
        return new AgentCapability<TState>(
            identity,
            name,
            receiptKind,
            tool,
            (state, kind, payload) =>
                kind == receiptKind
                    ? apply(
                        state,
                        payload.Deserialize<TRequest>(JsonSerializerOptions.Web)
                            ?? throw new InvalidDataException(
                                $"Capability '{name}' receipt payload is invalid."
                            )
                    )
                    : state
        );
    }

    private static CallToolResult Result(object payload, bool isError) =>
        new()
        {
            IsError = isError,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
            StructuredContent = JsonSerializer.SerializeToElement(payload),
        };
}

internal sealed class CapabilityMcpTool<TRequest>(
    string name,
    string description,
    Func<TRequest, IServiceProvider, CancellationToken, ValueTask<CallToolResult>> invoke
) : McpServerTool
    where TRequest : class
{
    private static readonly JsonSerializerOptions _jsonOptions = JsonSerializerOptions.Web;

    public override Tool ProtocolTool { get; } = CreateProtocolTool(name, description);

    public override IReadOnlyList<object> Metadata => [];

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

    private static Tool CreateProtocolTool(string toolName, string toolDescription)
    {
        var schema = CreateInputSchema();
        return new Tool
        {
            Name = toolName,
            Description = toolDescription,
            InputSchema = schema,
        };
    }

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default
    )
    {
        var input =
            JsonSerializer.Deserialize<TRequest>(
                JsonSerializer.SerializeToElement(request.Params?.Arguments, _jsonOptions),
                _jsonOptions
            ) ?? throw new InvalidOperationException($"Capability '{name}' received no request.");
        return invoke(
            input,
            request.Services
                ?? throw new InvalidOperationException(
                    "Capability request has no service provider."
                ),
            cancellationToken
        );
    }
}
