using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Tandem.NodeApiSpike;

internal static partial class RegistrationContractValidator
{
    private static readonly JsonSerializerOptions _options = new(JsonSerializerOptions.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentVariableName();

    public static RegisteredGraphContract ParseAndValidate(string definitionJson)
    {
        RegisteredGraphContract? graph;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(definitionJson);
            graph = JsonSerializer.Deserialize<RegisteredGraphContract>(definitionJson, _options);
        }
        catch (JsonException exception)
        {
            throw Invalid($"registration JSON is invalid: {exception.Message}");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw Invalid("registration must be a JSON object.");
        }
        if (graph is null)
            throw Invalid("registration must not be null.");

        var errors = new List<string>();
        if (graph.ContractVersion != 2)
            errors.Add($"contractVersion must be 2; received {graph.ContractVersion}.");
        Required(errors, "name", graph.Name);
        Required(errors, "start", graph.Start);
        Required(errors, "initialState", graph.InitialState);
        Json(errors, "initialState", graph.InitialState, objectRoot: false);
        if (graph.Nodes is null)
            errors.Add("nodes is required and must not be null.");
        if (graph.Routes is null)
            errors.Add("routes is required and must not be null.");
        if (graph.Outputs is null)
            errors.Add("outputs is required and must not be null.");
        if (graph.Callbacks is null)
            errors.Add("callbacks is required and must not be null.");
        if (graph.Outputs is { Length: 0 })
            errors.Add("outputs must contain at least one node ID.");
        if (graph.LedgerPath is not null && string.IsNullOrWhiteSpace(graph.LedgerPath))
            errors.Add("ledgerPath must be non-blank when provided.");

        var callbacks = Unique(errors, "callbacks", graph.Callbacks);
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var nodes = new Dictionary<string, RegisteredNodeContract>(StringComparer.Ordinal);
        foreach (var (node, index) in (graph.Nodes ?? []).Select((value, index) => (value, index)))
        {
            var path = $"nodes[{index}]";
            if (node is null)
            {
                errors.Add($"{path} must not be null.");
                continue;
            }
            Required(errors, $"{path}.id", node.Id);
            Required(errors, $"{path}.kind", node.Kind);
            if (!string.IsNullOrWhiteSpace(node.Id) && !nodes.TryAdd(node.Id, node))
                errors.Add($"{path}.id duplicates node ID '{node.Id}'.");
            ValidateNode(errors, callbacks, referenced, node, path);
        }
        if (!string.IsNullOrWhiteSpace(graph.Start) && !nodes.ContainsKey(graph.Start))
            errors.Add($"start references unknown node '{graph.Start}'.");

        Unique(errors, "outputs", graph.Outputs);
        foreach (var (id, index) in (graph.Outputs ?? []).Select((value, index) => (value, index)))
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (!nodes.TryGetValue(id, out var node))
                errors.Add($"outputs[{index}] references unknown node '{id}'.");
            else if (node.Kind is not ("completion" or "failure"))
                errors.Add($"outputs[{index}] node '{id}' must be a completion or failure.");
        }

        foreach (
            var (route, index) in (graph.Routes ?? []).Select((value, index) => (value, index))
        )
        {
            var path = $"routes[{index}]";
            if (route is null)
            {
                errors.Add($"{path} must not be null.");
                continue;
            }
            Required(errors, $"{path}.source", route.Source);
            Required(errors, $"{path}.target", route.Target);
            Required(errors, $"{path}.label", route.Label);
            Reference(errors, nodes, $"{path}.source", route.Source);
            Reference(errors, nodes, $"{path}.target", route.Target);
            Callback(
                errors,
                callbacks,
                referenced,
                $"{path}.predicateCallback",
                route.PredicateCallback,
                false
            );
            if (route.Outcome is not null && route.Outcome is not ("success" or "failed"))
                errors.Add($"{path}.outcome must be 'success' or 'failed'.");
            if (
                !string.IsNullOrWhiteSpace(route.Source)
                && nodes.TryGetValue(route.Source, out var source)
            )
            {
                if (source.Kind == "agent" && route.Outcome is null)
                    errors.Add($"{path}.outcome is required for agent source '{route.Source}'.");
                if (source.Kind != "agent" && route.Outcome is not null)
                    errors.Add(
                        $"{path}.outcome is forbidden for {source.Kind} source '{route.Source}'."
                    );
                if (source.Kind is "completion" or "failure")
                    errors.Add(
                        $"{path}.source terminal '{route.Source}' cannot have outgoing routes."
                    );
            }
        }
        foreach (
            var (callback, index) in (graph.Callbacks ?? []).Select(
                (value, index) => (value, index)
            )
        )
            if (!string.IsNullOrWhiteSpace(callback) && !referenced.Contains(callback))
                errors.Add($"callbacks[{index}] '{callback}' is not referenced by the contract.");

        if (errors.Count > 0)
            throw Invalid(string.Join("\n", errors.Select(error => $"- {error}")));
        return graph;
    }

    private static void ValidateNode(
        List<string> errors,
        HashSet<string> callbacks,
        HashSet<string> referenced,
        RegisteredNodeContract node,
        string path
    )
    {
        if (node.Kind is not ("stage" or "interaction" or "agent" or "completion" or "failure"))
        {
            if (!string.IsNullOrWhiteSpace(node.Kind))
                errors.Add($"{path}.kind '{node.Kind}' is unsupported.");
            return;
        }
        Field(
            errors,
            callbacks,
            referenced,
            path,
            "runCallback",
            node.RunCallback,
            node.Kind == "stage"
        );
        Field(
            errors,
            callbacks,
            referenced,
            path,
            "requestCallback",
            node.RequestCallback,
            node.Kind == "interaction"
        );
        Field(
            errors,
            callbacks,
            referenced,
            path,
            "handleCallback",
            node.HandleCallback,
            node.Kind == "interaction"
        );
        Field(
            errors,
            callbacks,
            referenced,
            path,
            "applyCallback",
            node.ApplyCallback,
            node.Kind == "interaction"
        );
        Field(
            errors,
            callbacks,
            referenced,
            path,
            "summaryCallback",
            node.SummaryCallback,
            node.Kind is "completion" or "failure"
        );
        Field(
            errors,
            callbacks,
            referenced,
            path,
            "messageCallback",
            node.MessageCallback,
            node.Kind == "agent"
        );
        if (node.Kind == "agent")
            Required(errors, $"{path}.instructions", node.Instructions);
        else if (node.Instructions is not null)
            errors.Add($"{path}.instructions is forbidden.");
        if (node.Kind == "agent")
            ValidateAgent(errors, callbacks, referenced, node, path);
        else
        {
            if (node.Client is not null)
                errors.Add($"{path}.client is forbidden.");
            if (node.Output is not null)
                errors.Add($"{path}.output is forbidden.");
            if (node.Capabilities is not null)
                errors.Add($"{path}.capabilities is forbidden.");
            if (node.ContinueSession)
                errors.Add($"{path}.continueSession is forbidden for kind '{node.Kind}'.");
            if (node.TimeoutMilliseconds is not null)
                errors.Add($"{path}.timeoutMilliseconds is forbidden for kind '{node.Kind}'.");
        }
        if (
            node.TimeoutMilliseconds is { } timeout
            && (!double.IsFinite(timeout) || timeout <= 0 || timeout > uint.MaxValue - 1)
        )
            errors.Add(
                $"{path}.timeoutMilliseconds must be a finite positive number no greater than {uint.MaxValue - 1}."
            );
    }

    private static void ValidateAgent(
        List<string> errors,
        HashSet<string> callbacks,
        HashSet<string> referenced,
        RegisteredNodeContract node,
        string path
    )
    {
        if (node.Client is null)
            errors.Add($"{path}.client is required.");
        else
            ValidateClient(errors, node.Client, $"{path}.client");
        if (node.Capabilities is null)
            errors.Add($"{path}.capabilities is required and must not be null.");
        if (node.Output is { } output)
        {
            Required(errors, $"{path}.output.contractName", output.ContractName);
            Json(errors, $"{path}.output.jsonSchema", output.JsonSchema, objectRoot: true);
            Callback(
                errors,
                callbacks,
                referenced,
                $"{path}.output.validateCallback",
                output.ValidateCallback,
                true
            );
            Callback(
                errors,
                callbacks,
                referenced,
                $"{path}.output.applyCallback",
                output.ApplyCallback,
                true
            );
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            var (capability, index) in (node.Capabilities ?? []).Select(
                (value, index) => (value, index)
            )
        )
        {
            var capabilityPath = $"{path}.capabilities[{index}]";
            if (capability is null)
            {
                errors.Add($"{capabilityPath} must not be null.");
                continue;
            }
            Required(errors, $"{capabilityPath}.name", capability.Name);
            if (!string.IsNullOrWhiteSpace(capability.Name) && !names.Add(capability.Name))
                errors.Add($"{capabilityPath}.name duplicates capability '{capability.Name}'.");
            Required(errors, $"{capabilityPath}.contractName", capability.ContractName);
            Json(errors, $"{capabilityPath}.jsonSchema", capability.JsonSchema, objectRoot: true);
            Callback(
                errors,
                callbacks,
                referenced,
                $"{capabilityPath}.validateCallback",
                capability.ValidateCallback,
                true
            );
            Callback(
                errors,
                callbacks,
                referenced,
                $"{capabilityPath}.applyCallback",
                capability.ApplyCallback,
                true
            );
            Callback(
                errors,
                callbacks,
                referenced,
                $"{capabilityPath}.summaryCallback",
                capability.SummaryCallback,
                true
            );
        }
    }

    private static void ValidateClient(
        List<string> errors,
        RegisteredChatClientContract client,
        string path
    )
    {
        if (client.Kind != "openai-compatible")
            errors.Add($"{path}.kind must be 'openai-compatible'.");
        if (client.Version != 1)
            errors.Add($"{path}.version must be 1.");
        Required(errors, $"{path}.endpoint", client.Endpoint);
        Required(errors, $"{path}.model", client.Model);
        if (client.WireApi is not ("completions" or "responses"))
            errors.Add($"{path}.wireApi must be 'completions' or 'responses'.");
        if (
            client.ReasoningEffort is not null
            && client.ReasoningEffort is not ("low" or "medium" or "high")
        )
            errors.Add($"{path}.reasoningEffort must be 'low', 'medium', or 'high'.");
        if (
            client.ApiKeyEnvironmentVariable is not null
            && !EnvironmentVariableName().IsMatch(client.ApiKeyEnvironmentVariable)
        )
            errors.Add(
                $"{path}.apiKeyEnvironmentVariable must be a valid environment-variable name."
            );
        if (
            !Uri.TryCreate(client.Endpoint, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
        )
            errors.Add($"{path}.endpoint must be an absolute HTTP(S) URI.");
        else if (
            !IsLoopback(endpoint.Host)
            && string.IsNullOrWhiteSpace(client.ApiKeyEnvironmentVariable)
        )
            errors.Add($"{path}.apiKeyEnvironmentVariable is required for non-loopback endpoints.");
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (
            System.Net.IPAddress.TryParse(host, out var address)
            && System.Net.IPAddress.IsLoopback(address)
        );

    private static void Field(
        List<string> errors,
        HashSet<string> callbacks,
        HashSet<string> referenced,
        string path,
        string name,
        string? value,
        bool required
    )
    {
        if (!required && value is not null)
            errors.Add($"{path}.{name} is forbidden.");
        else
            Callback(errors, callbacks, referenced, $"{path}.{name}", value, required);
    }

    private static void Callback(
        List<string> errors,
        HashSet<string> callbacks,
        HashSet<string> referenced,
        string path,
        string? value,
        bool required
    )
    {
        if (required)
            Required(errors, path, value);
        if (string.IsNullOrWhiteSpace(value))
            return;
        referenced.Add(value);
        if (!callbacks.Contains(value))
            errors.Add($"{path} references undeclared callback '{value}'.");
    }

    private static void Required(List<string> errors, string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{path} is required and must be non-blank.");
    }

    private static void Reference(
        List<string> errors,
        Dictionary<string, RegisteredNodeContract> nodes,
        string path,
        string? id
    )
    {
        if (!string.IsNullOrWhiteSpace(id) && !nodes.ContainsKey(id))
            errors.Add($"{path} references unknown node '{id}'.");
    }

    private static HashSet<string> Unique(List<string> errors, string path, string[]? values)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (value, index) in (values ?? []).Select((value, index) => (value, index)))
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"{path}[{index}] must be non-blank.");
            else if (!result.Add(value))
                errors.Add($"{path}[{index}] duplicates '{value}'.");
        }
        return result;
    }

    private static void Json(List<string> errors, string path, string? value, bool objectRoot)
    {
        Required(errors, path, value);
        if (string.IsNullOrWhiteSpace(value))
            return;
        try
        {
            using var document = JsonDocument.Parse(value);
            if (
                objectRoot
                && (
                    document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("type", out var type)
                    || type.GetString() != "object"
                )
            )
                errors.Add($"{path} must declare a JSON Schema object root with type 'object'.");
        }
        catch (JsonException)
        {
            errors.Add($"{path} must contain valid JSON.");
        }
    }

    private static InvalidOperationException Invalid(string detail) =>
        new($"Invalid registration contract:\n{detail}");
}
