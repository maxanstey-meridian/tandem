using System.Collections.Concurrent;
using System.Text.Json;

namespace Tandem.Bridge;

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
    public static RegisteredParticipant Create(
        CollectionNodeContract node,
        CallbackDispatcher callbacks
    )
    {
        var owned = node
            .Agents.Select(agent =>
                (RegisteredStandard)RegisteredParticipantFactory.Create(agent, callbacks)
            )
            .ToArray();
        var agents = new Dictionary<string, AgentDefinition<JavaScriptState>>(
            StringComparer.Ordinal
        );
        var collection = PipelineCollection.Create<
            JavaScriptState,
            JavaScriptState,
            JavaScriptState
        >(
            node.Id,
            state =>
            {
                using var json = JsonDocument.Parse(
                    callbacks.Invoke(node.ItemsCallback, state.Json, "")
                );
                return json
                    .RootElement.EnumerateArray()
                    .Select(item => new JavaScriptState(item.GetRawText()))
                    .ToArray();
            },
            owned.Select(value => (ICollectionAgent)value.Standard).ToArray(),
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
                            node.RunCallback,
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
                        node.ApplyCallback,
                        state.Json,
                        "[" + string.Join(",", results.Select(result => result.Json)) + "]"
                    )
                ),
            node.Max
        );
        foreach (var value in owned)
        {
            agents.Add(value.Contract.Id, (AgentDefinition<JavaScriptState>)value.Standard);
        }
        var bindings = (
            (CollectionDescriptor<JavaScriptState, JavaScriptState, JavaScriptState>)
                collection.Descriptor
        ).Bindings;
        var boundOwned = owned
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
