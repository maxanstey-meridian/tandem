namespace Tandem.Infrastructure.Blocks;

internal sealed class ToolOutcomeCollector
{
    internal readonly record struct ToolInvocationReservation(int Ordinal);

    private readonly object _sync = new();
    private string? _lifecycleToolName;
    private readonly Dictionary<
        string,
        (int Ordinal, ToolObservationDescriptor? Observation)
    > _latestToolOutcomes = [];
    private readonly List<ToolInvocationObservationDescriptor?> _toolInvocations = [];

    public ToolOutcomeCollector(
        IReadOnlyList<ToolInvocationObservationDescriptor>? priorInvocations = null
    )
    {
        if (priorInvocations is not null)
        {
            _toolInvocations.AddRange(priorInvocations);
        }
    }

    public bool HasLifecycleCall
    {
        get
        {
            lock (_sync)
            {
                return _lifecycleToolName is not null;
            }
        }
    }

    public void RecordLifecycleCall(string toolName)
    {
        lock (_sync)
        {
            _lifecycleToolName ??= toolName;
        }
    }

    public void RecordSuccessfulToolCall(
        ToolInvocationReservation reservation,
        ToolObservationDescriptor observation
    )
    {
        lock (_sync)
        {
            RecordToolOutcome(reservation, observation.Name, observation);
        }
    }

    public void RecordFailedToolCall(ToolInvocationReservation reservation, string toolName)
    {
        lock (_sync)
        {
            RecordToolOutcome(reservation, toolName, null);
        }
    }

    private void RecordToolOutcome(
        ToolInvocationReservation reservation,
        string toolName,
        ToolObservationDescriptor? observation
    )
    {
        if (
            !_latestToolOutcomes.TryGetValue(toolName, out var latest)
            || reservation.Ordinal > latest.Ordinal
        )
        {
            _latestToolOutcomes[toolName] = (reservation.Ordinal, observation);
        }
    }

    public ToolInvocationReservation ReserveToolInvocation()
    {
        lock (_sync)
        {
            var reservation = new ToolInvocationReservation(_toolInvocations.Count);
            _toolInvocations.Add(null);
            return reservation;
        }
    }

    public void CompleteToolInvocation(
        ToolInvocationReservation reservation,
        ToolInvocationObservationDescriptor observation
    )
    {
        lock (_sync)
        {
            _toolInvocations[reservation.Ordinal] = observation;
        }
    }

    public IReadOnlySet<ToolObservationDescriptor> SuccessfulTools
    {
        get
        {
            lock (_sync)
            {
                return _latestToolOutcomes
                    .Values.Select(value => value.Observation)
                    .Where(observation => observation is not null)
                    .Select(observation => observation!)
                    .ToHashSet();
            }
        }
    }

    public IReadOnlyList<ToolInvocationObservationDescriptor> ToolInvocations
    {
        get
        {
            lock (_sync)
            {
                return _toolInvocations
                    .Where(observation => observation is not null)
                    .Select(observation => observation!)
                    .ToArray();
            }
        }
    }
}
