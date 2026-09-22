using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem;

public interface ICollectionAgent : IPipelineNode;

internal interface ICollectionAgentBinding
{
    public ICollectionAgent BindTo(string id);
}

public static class PipelineCollection
{
    public static PipelineCollection<TState, TItem, TResult> Create<TState, TItem, TResult>(
        string id,
        Func<TState, IReadOnlyList<TItem>> items,
        IReadOnlyList<ICollectionAgent> agents,
        Func<TItem, CollectionContext, CancellationToken, ValueTask<TResult>> execute,
        Func<TState, IReadOnlyList<TResult>, TState> apply,
        int max
    ) => new(id, items, agents, execute, apply, max);
}

public sealed class PipelineCollection<TState, TItem, TResult>
    : IGeneratedPipelineStep<TState, GeneratedStepCompletion>
{
    internal PipelineCollection(
        string id,
        Func<TState, IReadOnlyList<TItem>> items,
        IReadOnlyList<ICollectionAgent> agents,
        Func<TItem, CollectionContext, CancellationToken, ValueTask<TResult>> execute,
        Func<TState, IReadOnlyList<TResult>, TState> apply,
        int max
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(execute);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(max);
        if (
            agents.Select(agent => agent.Id).Distinct(StringComparer.Ordinal).Count()
            != agents.Count
        )
        {
            throw new ArgumentException("Collection agent IDs must be unique.", nameof(agents));
        }
        Id = id;
        Descriptor = new CollectionDescriptor<TState, TItem, TResult>(
            id,
            items,
            agents.ToArray(),
            execute,
            apply,
            max
        );
    }

    public string Id { get; }
    public PipelineNodeDescriptor Descriptor { get; }
}

internal interface ICollectionDescriptor
{
    public IReadOnlyList<ICollectionAgent> Agents { get; }
    public int Max { get; }
}

internal sealed class CollectionDescriptor<TState, TItem, TResult>(
    string id,
    Func<TState, IReadOnlyList<TItem>> items,
    IReadOnlyList<ICollectionAgent> agents,
    Func<TItem, CollectionContext, CancellationToken, ValueTask<TResult>> execute,
    Func<TState, IReadOnlyList<TResult>, TState> apply,
    int max
) : PipelineNodeDescriptor, ICollectionDescriptor
{
    internal IReadOnlyDictionary<ICollectionAgent, ICollectionAgent> Bindings { get; } =
        agents.ToDictionary(
            agent => agent,
            agent => ((ICollectionAgentBinding)agent).BindTo($"{id}/{agent.Id}"),
            (IEqualityComparer<ICollectionAgent>)ReferenceEqualityComparer.Instance
        );
    public IReadOnlyList<ICollectionAgent> Agents => Bindings.Values.ToArray();
    public int Max => max;

    internal override ExecutorBinding Bind() =>
        new GeneratedStateStepExecutor<TState>(id, ExecuteAsync).Bind();

    private async ValueTask<TState> ExecuteAsync(TState state, CancellationToken cancellationToken)
    {
        var message = PipelineExecutionEnvelope.Get(state);
        var selected = items(state).ToArray();
        var results = new TResult[selected.Length];
        var occurrence = message.Runtime.NextInvocationId(id);
        PipelineExecutionEnvelope.Set(
            message with
            {
                Runtime = message.Runtime.IncrementInvocations(id),
            }
        );
        await Parallel.ForEachAsync(
            Enumerable.Range(0, selected.Length),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = max,
                CancellationToken = cancellationToken,
            },
            async (index, token) =>
            {
                var scope = new CollectionContext(
                    Bindings,
                    PipelineRuntime.Create(message.Runtime.RunId) with
                    {
                        InvocationScope = $"{occurrence}/item-{index}",
                    },
                    message.RunContext,
                    token
                );
                try
                {
                    results[index] = await execute(selected[index], scope, token);
                    scope.EnsureIdle();
                }
                finally
                {
                    await scope.CloseAsync();
                }
            }
        );
        cancellationToken.ThrowIfCancellationRequested();
        return apply(state, results);
    }
}

public sealed class CollectionContext
{
    private readonly IReadOnlyDictionary<ICollectionAgent, ICollectionAgent> _agents;
    private readonly PipelineRunContext? _runContext;
    private readonly CancellationTokenSource _cancellation;
    private PipelineRuntime _runtime;
    private Task? _pending;
    private int _active;
    private bool _disposed;

    internal CollectionContext(
        IReadOnlyDictionary<ICollectionAgent, ICollectionAgent> agents,
        PipelineRuntime runtime,
        PipelineRunContext? runContext,
        CancellationToken cancellationToken
    )
    {
        _agents = agents;
        _runtime = runtime;
        _runContext = runContext;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

    public ValueTask<TState> RunAsync<TState>(AgentDefinition<TState> agent, TState state)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _cancellation.Token.ThrowIfCancellationRequested();
        if (!_agents.TryGetValue(agent, out var bound))
        {
            throw new InvalidOperationException(
                $"Agent '{agent.Id}' is not declared in this collection."
            );
        }
        if (_pending is { IsCompleted: true })
        {
            _pending.GetAwaiter().GetResult();
        }
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InvalidOperationException("Collection item agents must be awaited serially.");
        }
        var pending = ExecuteAsync((AgentDefinition<TState>)bound, state);
        _pending = pending;
        return new(pending);
    }

    private async Task<TState> ExecuteAsync<TState>(AgentDefinition<TState> agent, TState state)
    {
        try
        {
            _runtime = _runtime with { ObservationVisitId = _runtime.NextInvocationId(agent.Id) };
            var descriptor = (GeneratedOutcomeStepDescriptor<TState>)agent.Descriptor;
            var result = await descriptor.ExecuteScopedAsync(
                new PipelineMessage<TState>(_runtime, state) { RunContext = _runContext },
                _cancellation.Token
            );
            _runtime = result.Runtime;
            if (result.Status == PipelineRunStatus.Failed)
            {
                throw new InvalidOperationException(
                    result.LatestOutcome?.Summary ?? "Collection agent failed."
                );
            }
            return result.State;
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    internal void EnsureIdle()
    {
        if (Volatile.Read(ref _active) != 0)
        {
            throw new InvalidOperationException(
                "Collection item returned before its agent completed."
            );
        }
        _pending?.GetAwaiter().GetResult();
    }

    internal async ValueTask CloseAsync()
    {
        _disposed = true;
        await _cancellation.CancelAsync();
        if (_pending is not null)
        {
            try
            {
                await _pending;
            }
            catch { }
        }
        _cancellation.Dispose();
    }
}
