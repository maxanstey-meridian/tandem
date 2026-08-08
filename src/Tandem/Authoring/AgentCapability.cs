using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Tandem;

internal sealed record AgentCapabilityDescriptor<TState>(
    string CapabilityId,
    string ToolName,
    Func<CapabilityInvocationState<TState>, AIFunction> Bind
);

internal sealed record AcceptedCapability<TState>(
    string CapabilityId,
    string ToolName,
    TState State,
    string Summary,
    JsonElement Payload
);

internal sealed class CapabilityInvocationState<TState>(
    Guid runId,
    string blockId,
    string invocationId,
    TState state
)
{
    private readonly object _sync = new();
    private bool _reserved;

    public Guid RunId { get; } = runId;
    public string BlockId { get; } = blockId;
    public string InvocationId { get; } = invocationId;
    public TState State { get; } = state;
    public AcceptedCapability<TState>? Accepted { get; private set; }

    public bool TryReserve()
    {
        lock (_sync)
        {
            if (_reserved || Accepted is not null)
            {
                return false;
            }
            _reserved = true;
            return true;
        }
    }

    public void Release()
    {
        lock (_sync)
        {
            _reserved = false;
        }
    }

    public void Commit(AcceptedCapability<TState> accepted)
    {
        lock (_sync)
        {
            if (!_reserved || Accepted is not null)
            {
                throw new InvalidOperationException("Capability acceptance is not reserved.");
            }
            Accepted = accepted;
            _reserved = false;
        }
    }
}
