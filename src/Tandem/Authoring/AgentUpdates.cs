using System.Collections.Concurrent;

namespace Tandem;

public static class AgentUpdates
{
    private static readonly ConcurrentDictionary<
        Guid,
        Action<string, Guid, AgentUpdate>
    > _observers = new();

    public static IDisposable Observe(Guid runId, Action<string, Guid, AgentUpdate> observer)
    {
        if (!_observers.TryAdd(runId, observer))
        {
            throw new InvalidOperationException(
                $"Agent updates are already observed for run '{runId}'."
            );
        }
        return new Observation(runId);
    }

    internal static void Publish(string blockId, Guid runId, AgentUpdate update)
    {
        if (_observers.TryGetValue(runId, out var observer))
        {
            observer(blockId, runId, update);
        }
    }

    private sealed class Observation(Guid runId) : IDisposable
    {
        public void Dispose() => _observers.TryRemove(runId, out _);
    }
}
