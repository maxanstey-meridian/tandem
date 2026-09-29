using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tandem.NodeApiSpike;

/// <summary>
/// Parses the registration contract into per-kind types and checks what only the whole
/// graph can know: references, outputs, reachability and callback identity. Range and
/// shape rules that Tandem's builders own are enforced when the graph is built and
/// surfaced through <see cref="CoreRule{T}"/>.
/// </summary>
internal static partial class RegistrationContractValidator
{
    private static readonly JsonSerializerOptions _options = new(TandemJson.CreateTypedContract())
    {
        AllowOutOfOrderMetadataProperties = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentVariableName();

    public static RegisteredGraphContract ParseAndValidate(string definitionJson)
    {
        RegisteredGraphContract graph;
        try
        {
            graph =
                JsonSerializer.Deserialize<RegisteredGraphContract>(definitionJson, _options)
                ?? throw Invalid("registration must not be null.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw Invalid($"registration JSON is invalid: {exception.Message}");
        }

        var errors = new List<string>();
        RejectNullEntries(errors, graph);
        if (errors.Count == 0)
        {
            ValidateGraph(errors, graph);
        }
        if (errors.Count > 0)
            throw Invalid(string.Join("\n", errors.Select(error => $"- {error}")));
        return graph;
    }

    /// <summary>Runs a Tandem builder step and reports its validation failure against the contract path.</summary>
    public static T CoreRule<T>(string path, Func<T> build)
    {
        try
        {
            return build();
        }
        catch (Exception exception)
            when (exception is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw Invalid($"- {path}: {exception.Message}");
        }
    }

    public static void CoreRule(string path, Action build) =>
        CoreRule(
            path,
            () =>
            {
                build();
                return true;
            }
        );

    public static IEnumerable<RegisteredNodeContract> EnumerateNodes(
        IEnumerable<RegisteredNodeContract> nodes
    )
    {
        foreach (var node in nodes)
        {
            yield return node;
            var children = node switch
            {
                ParallelNodeContract parallel => parallel.Branches.Select(branch =>
                    branch.Participant
                ),
                CollectionNodeContract collection => collection.Agents,
                _ => [],
            };
            foreach (var child in EnumerateNodes(children))
            {
                yield return child;
            }
        }
    }

    private static void ValidateGraph(List<string> errors, RegisteredGraphContract graph)
    {
        if (graph.ContractVersion != 10)
            errors.Add($"contractVersion must be 10; received {graph.ContractVersion}.");
        Required(errors, "name", graph.Name);
        Json(errors, "initialState", graph.InitialState);
        if (graph.Outputs.Length == 0)
            errors.Add("outputs must contain at least one node ID.");
        if (graph.LedgerPath is not null && string.IsNullOrWhiteSpace(graph.LedgerPath))
            errors.Add("ledgerPath must be non-blank when provided.");
        if (graph.Terminal is not null && graph.Presentation != RegisteredPresentation.Terminal)
            errors.Add("terminal options require terminal presentation.");
        Unique(errors, "terminal.truncatedToolNames", graph.Terminal?.TruncatedToolNames);
        if (
            graph.LedgerPath is null
            && (graph.Persist || EnumerateNodes(graph.Nodes).Any(node => node.Persist == true))
        )
            errors.Add("ledgerPath is required when persistence is enabled.");

        var nodes = new Dictionary<string, RegisteredNodeContract>(StringComparer.Ordinal);
        foreach (var (index, node) in graph.Nodes.Index())
        {
            if (!nodes.TryAdd(node.Id, node))
                errors.Add($"nodes[{index}].id duplicates node ID '{node.Id}'.");
            ValidateNode(errors, node, $"nodes[{index}]");
        }

        if (!nodes.TryGetValue(graph.Start, out var start))
            errors.Add($"start references unknown node '{graph.Start}'.");
        else if (start is TerminalNodeContract)
            errors.Add($"start node '{graph.Start}' cannot be a terminal.");

        Unique(errors, "outputs", graph.Outputs);
        foreach (var (index, id) in graph.Outputs.Index())
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (!nodes.TryGetValue(id, out var node))
                errors.Add($"outputs[{index}] references unknown node '{id}'.");
            else if (node is not TerminalNodeContract)
                errors.Add($"outputs[{index}] node '{id}' must be a completion or failure.");
        }

        foreach (var (index, route) in graph.Routes.Index())
            ValidateRoute(errors, nodes, route, $"routes[{index}]");
        ValidateInteractionHandlers(errors, nodes, graph.InteractionHandlers ?? []);
        ValidateCallbacks(errors, graph);
        ValidateReachability(errors, graph, nodes);
    }

    private static void ValidateNode(List<string> errors, RegisteredNodeContract node, string path)
    {
        switch (node)
        {
            case ParallelNodeContract parallel:
                foreach (var (index, branch) in parallel.Branches.Index())
                {
                    var participantPath = $"{path}.branches[{index}].participant";
                    if (branch.Participant is not (StageNodeContract or AgentNodeContract))
                        errors.Add(
                            $"{participantPath}.kind '{KindName(branch.Participant)}' is unsupported in a parallel branch."
                        );
                    ValidateNode(errors, branch.Participant, participantPath);
                }
                break;
            case CollectionNodeContract collection:
                foreach (var (index, agent) in collection.Agents.Index())
                {
                    if (agent is not AgentNodeContract)
                        errors.Add($"{path}.agents[{index}] must be an agent.");
                    ValidateNode(errors, agent, $"{path}.agents[{index}]");
                }
                break;
            case AgentNodeContract agent:
                ValidateAgent(errors, agent, path);
                break;
        }
    }

    private static void ValidateAgent(List<string> errors, AgentNodeContract agent, string path)
    {
        ValidateClient(errors, agent.Client, $"{path}.client");
        if (agent.Reasoning is { } reasoning)
        {
            // Core has no reasoning object to be empty; effort with maxTokens is Core's rule.
            if (reasoning.Effort is null && reasoning.MaxTokens is null)
                errors.Add($"{path}.reasoning must specify effort or maxTokens.");
            if (
                reasoning.MaxTokens is not null
                && agent.Client.WireApi != RegisteredWireApi.Completions
            )
                errors.Add($"{path}.reasoning.maxTokens requires a completions client.");
        }
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, capability) in agent.Capabilities.Index())
        {
            var capabilityPath = $"{path}.capabilities[{index}]";
            if (!capabilities.Add(capability.Name))
                errors.Add($"{capabilityPath}.name duplicates capability '{capability.Name}'.");
            Json(errors, $"{capabilityPath}.jsonSchema", capability.JsonSchema);
        }
        if (agent.Checkpoint is { } checkpoint && !capabilities.Contains(checkpoint.CapabilityName))
            errors.Add($"{path}.checkpoint.capabilityName must reference an attached capability.");
        if (agent.Output is { } output)
        {
            var outputPath = $"{path}.output";
            if (output.Raw)
            {
                if (output.RawParseCallback is null)
                    errors.Add($"{outputPath}.rawParseCallback is required for raw output.");
                if (output.JsonSchema is not null)
                    errors.Add($"{outputPath}.jsonSchema is forbidden for raw output.");
                if (output.ValidateCallback is not null)
                    errors.Add($"{outputPath}.validateCallback is forbidden for raw output.");
            }
            else
            {
                Json(errors, $"{outputPath}.jsonSchema", output.JsonSchema);
                if (output.ValidateCallback is null)
                    errors.Add($"{outputPath}.validateCallback is required.");
                if (output.RawParseCallback is not null)
                    errors.Add($"{outputPath}.rawParseCallback is forbidden.");
            }
        }
    }

    private static void ValidateRoute(
        List<string> errors,
        Dictionary<string, RegisteredNodeContract> nodes,
        RegisteredRouteContract route,
        string path
    )
    {
        Required(errors, $"{path}.label", route.Label);
        if (!nodes.ContainsKey(route.Target))
            errors.Add($"{path}.target references unknown node '{route.Target}'.");
        if (!nodes.TryGetValue(route.Source, out var source))
        {
            errors.Add($"{path}.source references unknown node '{route.Source}'.");
            return;
        }
        var routesByOutcome = source is AgentNodeContract or ParallelNodeContract;
        if (routesByOutcome && route.Outcome is null)
            errors.Add(
                $"{path}.outcome is required for {KindName(source)} source '{route.Source}'."
            );
        if (!routesByOutcome && route.Outcome is not null)
            errors.Add(
                $"{path}.outcome is forbidden for {KindName(source)} source '{route.Source}'."
            );
        if (source is TerminalNodeContract)
            errors.Add($"{path}.source terminal '{route.Source}' cannot have outgoing routes.");
    }

    private static void ValidateInteractionHandlers(
        List<string> errors,
        Dictionary<string, RegisteredNodeContract> nodes,
        RegisteredInteractionHandlerContract[] handlers
    )
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var targets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, handler) in handlers.Index())
        {
            var path = $"interactionHandlers[{index}]";
            Required(errors, $"{path}.id", handler.Id);
            if (!ids.Add(handler.Id))
                errors.Add($"{path}.id duplicates interaction handler ID '{handler.Id}'.");
            if (!targets.Add(handler.Target))
                errors.Add(
                    $"{path}.target duplicates interaction handler target '{handler.Target}'."
                );
            if (!nodes.TryGetValue(handler.Target, out var target))
                errors.Add($"{path}.target references unknown node '{handler.Target}'.");
            else if (target is not InteractionNodeContract)
                errors.Add($"{path}.target node '{handler.Target}' must be an interaction.");
        }
    }

    private static void ValidateReachability(
        List<string> errors,
        RegisteredGraphContract graph,
        Dictionary<string, RegisteredNodeContract> nodes
    )
    {
        if (!nodes.ContainsKey(graph.Start))
            return;
        var targetsBySource = graph
            .Routes.Where(route =>
                nodes.ContainsKey(route.Source) && nodes.ContainsKey(route.Target)
            )
            .ToLookup(route => route.Source, route => route.Target, StringComparer.Ordinal);
        var reachable = new HashSet<string>(StringComparer.Ordinal) { graph.Start };
        var pending = new Queue<string>([graph.Start]);
        while (pending.TryDequeue(out var source))
        {
            foreach (var target in targetsBySource[source])
            {
                if (reachable.Add(target))
                    pending.Enqueue(target);
            }
        }

        var outputs = graph.Outputs.ToHashSet(StringComparer.Ordinal);
        foreach (var terminal in reachable.Where(id => nodes[id] is TerminalNodeContract))
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

    private static void ValidateCallbacks(List<string> errors, RegisteredGraphContract graph)
    {
        var references = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, reference) in Callbacks(graph))
        {
            if (reference is null)
                continue;
            if (string.IsNullOrWhiteSpace(reference))
                errors.Add($"{path} must be non-blank.");
            else if (!references.TryAdd(reference, path))
                errors.Add(
                    $"{path} duplicates callback reference '{reference}' from {references[reference]}."
                );
        }
    }

    private static IEnumerable<(string Path, string? Reference)> Callbacks(
        RegisteredGraphContract graph
    )
    {
        yield return ("observationCallback", graph.ObservationCallback);
        foreach (var (index, node) in graph.Nodes.Index())
        {
            foreach (var callback in Callbacks(node, $"nodes[{index}]"))
                yield return callback;
        }
        foreach (var (index, route) in graph.Routes.Index())
            yield return ($"routes[{index}].predicateCallback", route.PredicateCallback);
        foreach (var (index, handler) in (graph.InteractionHandlers ?? []).Index())
            yield return ($"interactionHandlers[{index}].handleCallback", handler.HandleCallback);
    }

    private static IEnumerable<(string Path, string? Reference)> Callbacks(
        RegisteredNodeContract node,
        string path
    )
    {
        switch (node)
        {
            case StageNodeContract stage:
                yield return ($"{path}.runCallback", stage.RunCallback);
                break;
            case InteractionNodeContract interaction:
                yield return ($"{path}.requestCallback", interaction.RequestCallback);
                yield return ($"{path}.applyCallback", interaction.ApplyCallback);
                break;
            case TerminalNodeContract terminal:
                yield return ($"{path}.summaryCallback", terminal.SummaryCallback);
                break;
            case ParallelNodeContract parallel:
                yield return ($"{path}.mergeCallback", parallel.MergeCallback);
                foreach (var (index, branch) in parallel.Branches.Index())
                {
                    foreach (
                        var callback in Callbacks(
                            branch.Participant,
                            $"{path}.branches[{index}].participant"
                        )
                    )
                        yield return callback;
                }
                break;
            case CollectionNodeContract collection:
                yield return ($"{path}.itemsCallback", collection.ItemsCallback);
                yield return ($"{path}.runCallback", collection.RunCallback);
                yield return ($"{path}.applyCallback", collection.ApplyCallback);
                foreach (var (index, agent) in collection.Agents.Index())
                {
                    foreach (var callback in Callbacks(agent, $"{path}.agents[{index}]"))
                        yield return callback;
                }
                break;
            case AgentNodeContract agent:
                yield return ($"{path}.messageCallback", agent.MessageCallback);
                yield return (
                    $"{path}.checkpoint.messageCallback",
                    agent.Checkpoint?.MessageCallback
                );
                if (agent.Workspace is { } workspace)
                {
                    yield return ($"{path}.workspace.pathCallback", workspace.PathCallback);
                    yield return ($"{path}.workspace.commandsCallback", workspace.CommandsCallback);
                    yield return (
                        $"{path}.workspace.interceptCallback",
                        workspace.InterceptCallback
                    );
                    foreach (var (index, group) in workspace.ToolGroups.Index())
                        yield return (
                            $"{path}.workspace.toolGroups[{index}].whenCallback",
                            group.WhenCallback
                        );
                }
                if (agent.Output is { } output)
                {
                    yield return ($"{path}.output.validateCallback", output.ValidateCallback);
                    yield return ($"{path}.output.validateForCallback", output.ValidateForCallback);
                    yield return ($"{path}.output.rawParseCallback", output.RawParseCallback);
                    yield return ($"{path}.output.applyCallback", output.ApplyCallback);
                }
                foreach (var (index, capability) in agent.Capabilities.Index())
                {
                    var capabilityPath = $"{path}.capabilities[{index}]";
                    yield return (
                        $"{capabilityPath}.validateCallback",
                        capability.ValidateCallback
                    );
                    yield return (
                        $"{capabilityPath}.validateForCallback",
                        capability.ValidateForCallback
                    );
                    yield return ($"{capabilityPath}.applyCallback", capability.ApplyCallback);
                    yield return ($"{capabilityPath}.summaryCallback", capability.SummaryCallback);
                }
                break;
        }
    }

    private static void ValidateClient(
        List<string> errors,
        RegisteredChatClientContract client,
        string path
    )
    {
        if (
            client.RequestTimeoutMs is <= 0
            || client.IdleTimeoutMs is <= 0
            || client.MaxAttempts is <= 0
        )
            errors.Add(
                $"{path} requestTimeoutMs, idleTimeoutMs and maxAttempts must be positive integers."
            );
        if (client.Version != 1)
            errors.Add($"{path}.version must be 1.");
        Required(errors, $"{path}.model", client.Model);
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
        else if (!IsLoopback(endpoint.Host) && client.ApiKeyEnvironmentVariable is null)
            errors.Add($"{path}.apiKeyEnvironmentVariable is required for non-loopback endpoints.");
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (
            System.Net.IPAddress.TryParse(host, out var address)
            && System.Net.IPAddress.IsLoopback(address)
        );

    // System.Text.Json does not apply nullable annotations to array elements.
    private static void RejectNullEntries(List<string> errors, RegisteredGraphContract graph)
    {
        void Check<T>(string path, T?[]? items)
            where T : class
        {
            foreach (var (index, item) in (items ?? []).Index())
            {
                if (item is null)
                    errors.Add($"{path}[{index}] must not be null.");
            }
        }

        void CheckNode(RegisteredNodeContract? node, string path)
        {
            switch (node)
            {
                case ParallelNodeContract parallel:
                    Check($"{path}.branches", parallel.Branches);
                    foreach (var (index, branch) in parallel.Branches.Index())
                        CheckNode(branch?.Participant, $"{path}.branches[{index}].participant");
                    break;
                case CollectionNodeContract collection:
                    Check($"{path}.agents", collection.Agents);
                    foreach (var (index, agent) in collection.Agents.Index())
                        CheckNode(agent, $"{path}.agents[{index}]");
                    break;
                case AgentNodeContract agent:
                    Check($"{path}.capabilities", agent.Capabilities);
                    Check($"{path}.workspace.toolGroups", agent.Workspace?.ToolGroups);
                    break;
            }
        }

        Check("nodes", graph.Nodes);
        Check("routes", graph.Routes);
        Check("interactionHandlers", graph.InteractionHandlers);
        foreach (var (index, node) in graph.Nodes.Index())
            CheckNode(node, $"nodes[{index}]");
    }

    private static string KindName(RegisteredNodeContract node) =>
        node switch
        {
            StageNodeContract => "stage",
            InteractionNodeContract => "interaction",
            AgentNodeContract => "agent",
            ParallelNodeContract => "parallel",
            CollectionNodeContract => "collection",
            CompletionNodeContract => "completion",
            _ => "failure",
        };

    private static void Required(List<string> errors, string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{path} is required and must be non-blank.");
    }

    private static void Unique(List<string> errors, string path, string[]? values)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (index, value) in (values ?? []).Index())
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"{path}[{index}] must be non-blank.");
            else if (!seen.Add(value))
                errors.Add($"{path}[{index}] duplicates '{value}'.");
        }
    }

    private static void Json(List<string> errors, string path, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors.Add($"{path} is required and must be non-blank.");
            return;
        }
        try
        {
            using var _ = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            errors.Add($"{path} must contain valid JSON.");
        }
    }

    private static InvalidOperationException Invalid(string detail) =>
        new($"Invalid registration contract:\n{detail}");
}
