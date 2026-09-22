using System.Collections.Concurrent;
using System.Text.Json;

namespace Tandem.NodeApiSpike;

public static partial class NodePipelineBridge
{
    internal static readonly ConcurrentDictionary<
        string,
        Func<string, string, Task<string>>
    > CollectionScopes = new();

    /// <summary>Executes a declared agent within an active collection item.</summary>
    public static Task<string> RunCollectionAgentAsync(string scopeId, string agentId, string state)
    {
        if (!CollectionScopes.TryGetValue(scopeId, out var execute))
        {
            throw new InvalidOperationException("Collection scope has ended.");
        }
        return Task.Run(() => execute(agentId, state));
    }
}

internal static class RegisteredCollection
{
    public static async Task<RegisteredParticipant> CreateAsync(
        RegisteredNodeContract node,
        CallbackDispatcher callbacks,
        CancellationToken token
    )
    {
        var owned = new List<RegisteredParticipant>();
        foreach (var agent in node.Agents!)
        {
            owned.Add(await RegisteredParticipantFactory.CreateAsync(agent, callbacks, token));
        }
        var agents = owned
            .Cast<RegisteredStandard>()
            .ToDictionary(
                value => value.Contract.Id!,
                value => (AgentDefinition<JavaScriptState>)value.Standard,
                StringComparer.Ordinal
            );
        var collection = PipelineCollection.Create<
            JavaScriptState,
            JavaScriptState,
            JavaScriptState
        >(
            node.Id!,
            state =>
            {
                using var json = JsonDocument.Parse(
                    callbacks.Invoke(node.ItemsCallback!, state.Json, "")
                );
                return json
                    .RootElement.EnumerateArray()
                    .Select(item => new JavaScriptState(item.GetRawText()))
                    .ToArray();
            },
            agents.Values.Cast<ICollectionAgent>().ToArray(),
            async (item, scope, cancellationToken) =>
            {
                var key = Guid.NewGuid().ToString("N");
                NodePipelineBridge.CollectionScopes[key] = async (agentId, input) =>
                {
                    if (!agents.TryGetValue(agentId, out var agent))
                    {
                        throw new InvalidOperationException(
                            "Agent is not declared in this collection."
                        );
                    }
                    return (await scope.RunAsync(agent, new JavaScriptState(input))).Json;
                };
                try
                {
                    return new JavaScriptState(
                        await callbacks.InvokeAsync(
                            node.RunCallback!,
                            item.Json,
                            key,
                            cancellationToken
                        )
                    );
                }
                finally
                {
                    NodePipelineBridge.CollectionScopes.TryRemove(key, out _);
                }
            },
            (state, results) =>
                new JavaScriptState(
                    callbacks.Invoke(
                        node.ApplyCallback!,
                        state.Json,
                        "[" + string.Join(",", results.Select(result => result.Json)) + "]"
                    )
                ),
            node.Max!.Value
        );
        var bindings = (
            (CollectionDescriptor<JavaScriptState, JavaScriptState, JavaScriptState>)
                collection.Descriptor
        ).Bindings;
        var boundOwned = owned
            .Cast<RegisteredStandard>()
            .Select(value =>
            {
                var bound =
                    (AgentDefinition<JavaScriptState>)bindings[(ICollectionAgent)value.Standard];
                return value with
                {
                    Contract = value.Contract with { Id = bound.Id },
                    Standard = bound,
                    Success = bound.Success,
                    Failed = bound.Failed,
                };
            })
            .ToArray();
        return new RegisteredStage(node, collection, boundOwned);
    }
}
