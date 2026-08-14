using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Tandem;

internal sealed record AgentCapabilityDescriptor<TState>(
    string CapabilityId,
    string ToolName,
    Func<CapabilityInvocationState<TState>, AIFunction> Bind,
    Func<
        Func<CapabilityAcceptanceContext<TState, JsonElement>, CancellationToken, ValueTask>,
        AgentCapabilityDescriptor<TState>
    >? WithJsonAcceptance = null
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
    string stepId,
    string invocationId,
    TState state,
    PipelineRunContext? runContext = null
)
{
    private readonly object _sync = new();
    private bool _reserved;
    private Exception? _applicationFault;

    public Guid RunId { get; } = runId;
    public string StepId { get; } = stepId;
    public string InvocationId { get; } = invocationId;
    public TState State { get; } = state;
    public PipelineRunContext? RunContext { get; } = runContext;
    public AcceptedCapability<TState>? Accepted { get; private set; }
    public string? AcceptedCallId { get; private set; }
    public object? AcceptedResult { get; private set; }

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

    public void RecordResult(string callId, object? result)
    {
        lock (_sync)
        {
            if (Accepted is null)
            {
                throw new InvalidOperationException("Capability result requires acceptance.");
            }
            AcceptedCallId = callId;
            AcceptedResult = result;
        }
    }

    public void RecordApplicationFault(Exception exception)
    {
        lock (_sync)
        {
            _applicationFault ??= exception;
        }
    }

    public void ThrowIfApplicationFaulted()
    {
        Exception? fault;
        lock (_sync)
        {
            fault = _applicationFault;
        }
        if (fault is not null)
        {
            ExceptionDispatchInfo.Capture(fault).Throw();
        }
    }
}
