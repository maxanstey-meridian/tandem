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
        if (graph.ContractVersion != 3)
            errors.Add($"contractVersion must be 3; received {graph.ContractVersion}.");
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
        if (graph.Outputs is { Length: 0 })
            errors.Add("outputs must contain at least one node ID.");
        if (graph.LedgerPath is not null && string.IsNullOrWhiteSpace(graph.LedgerPath))
            errors.Add("ledgerPath must be non-blank when provided.");
        if (
            graph.LedgerPath is null
            && (graph.Persist || (graph.Nodes ?? []).Any(node => node?.Persist == true))
        )
            errors.Add("ledgerPath is required when persistence is enabled.");

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
            ValidateNode(errors, node, path);
        }
        if (!string.IsNullOrWhiteSpace(graph.Start) && !nodes.ContainsKey(graph.Start))
            errors.Add($"start references unknown node '{graph.Start}'.");
        else if (
            !string.IsNullOrWhiteSpace(graph.Start)
            && nodes[graph.Start].Kind is "completion" or "failure"
        )
            errors.Add($"start node '{graph.Start}' cannot be a terminal.");

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
            OptionalCallback(errors, $"{path}.predicateCallback", route.PredicateCallback);
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
        ValidateGraphShape(errors, graph, nodes);
        if (errors.Count > 0)
            throw Invalid(string.Join("\n", errors.Select(error => $"- {error}")));
        return graph;
    }

    private static void ValidateGraphShape(
        List<string> errors,
        RegisteredGraphContract graph,
        IReadOnlyDictionary<string, RegisteredNodeContract> nodes
    )
    {
        var validRoutes = (graph.Routes ?? []).Where(route =>
            route is not null
            && !string.IsNullOrWhiteSpace(route.Source)
            && !string.IsNullOrWhiteSpace(route.Target)
            && nodes.ContainsKey(route.Source)
            && nodes.ContainsKey(route.Target)
        );
        foreach (var group in validRoutes.GroupBy(route => (route!.Source!, route.Outcome)))
        {
            if (group.Count(route => route!.PredicateCallback is null) > 1)
            {
                errors.Add(
                    $"routes from '{group.Key.Item1}' for outcome '{group.Key.Outcome ?? "default"}' contain more than one unconditional route."
                );
            }
        }

        if (string.IsNullOrWhiteSpace(graph.Start) || !nodes.ContainsKey(graph.Start))
            return;
        var reachable = new HashSet<string>(StringComparer.Ordinal) { graph.Start };
        var pending = new Queue<string>();
        pending.Enqueue(graph.Start);
        var bySource = validRoutes
            .GroupBy(route => route!.Source!)
            .ToDictionary(group => group.Key);
        while (pending.TryDequeue(out var source))
        {
            if (!bySource.TryGetValue(source, out var routes))
                continue;
            foreach (var route in routes)
            {
                if (reachable.Add(route!.Target!))
                    pending.Enqueue(route.Target!);
            }
        }

        var outputs = (graph.Outputs ?? []).ToHashSet(StringComparer.Ordinal);
        foreach (var terminal in reachable.Where(id => nodes[id].Kind is "completion" or "failure"))
        {
            if (!outputs.Contains(terminal))
                errors.Add($"reachable terminal '{terminal}' must be listed in outputs.");
        }
        foreach (var output in outputs.Where(nodes.ContainsKey))
        {
            if (!reachable.Contains(output))
                errors.Add($"output '{output}' is unreachable from start '{graph.Start}'.");
        }
    }

    private static void ValidateNode(List<string> errors, RegisteredNodeContract node, string path)
    {
        if (node.Kind is not ("stage" or "interaction" or "agent" or "completion" or "failure"))
        {
            if (!string.IsNullOrWhiteSpace(node.Kind))
                errors.Add($"{path}.kind '{node.Kind}' is unsupported.");
            return;
        }
        Field(errors, path, "runCallback", node.RunCallback, node.Kind == "stage");
        Field(errors, path, "requestCallback", node.RequestCallback, node.Kind == "interaction");
        Field(errors, path, "handleCallback", node.HandleCallback, node.Kind == "interaction");
        Field(errors, path, "applyCallback", node.ApplyCallback, node.Kind == "interaction");
        Field(
            errors,
            path,
            "summaryCallback",
            node.SummaryCallback,
            node.Kind is "completion" or "failure"
        );
        Field(errors, path, "messageCallback", node.MessageCallback, node.Kind == "agent");
        if (node.Kind == "agent")
            Required(errors, $"{path}.instructions", node.Instructions);
        else if (node.Instructions is not null)
            errors.Add($"{path}.instructions is forbidden.");
        if (node.Kind == "agent")
            ValidateAgent(errors, node, path);
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

    private static void ValidateAgent(List<string> errors, RegisteredNodeContract node, string path)
    {
        if (node.Client is null)
            errors.Add($"{path}.client is required.");
        else
            ValidateClient(errors, node.Client, $"{path}.client");
        if (node.Capabilities is null)
            errors.Add($"{path}.capabilities is required and must not be null.");
        if (node.Output is { } output)
        {
            Required(errors, $"{path}.output.valueType", output.ValueType);
            Json(errors, $"{path}.output.jsonSchema", output.JsonSchema, objectRoot: true);
            Required(errors, $"{path}.output.validateCallback", output.ValidateCallback);
            Required(errors, $"{path}.output.applyCallback", output.ApplyCallback);
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
            Required(errors, $"{capabilityPath}.valueType", capability.ValueType);
            Json(errors, $"{capabilityPath}.jsonSchema", capability.JsonSchema, objectRoot: true);
            Required(errors, $"{capabilityPath}.validateCallback", capability.ValidateCallback);
            Required(errors, $"{capabilityPath}.applyCallback", capability.ApplyCallback);
            Required(errors, $"{capabilityPath}.summaryCallback", capability.SummaryCallback);
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
        string path,
        string name,
        string? value,
        bool required
    )
    {
        if (!required && value is not null)
            errors.Add($"{path}.{name} is forbidden.");
        else if (required)
            Required(errors, $"{path}.{name}", value);
    }

    private static void OptionalCallback(List<string> errors, string path, string? value)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            errors.Add($"{path} must be non-blank when provided.");
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
