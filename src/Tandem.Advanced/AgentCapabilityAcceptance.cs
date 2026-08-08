namespace Tandem.Advanced;

public sealed record AgentCapabilityAcceptanceContext<TState, TRequest>(
    Guid RunId,
    string BlockId,
    string InvocationId,
    string CapabilityId,
    TState State,
    TRequest Request
)
{
    public string AcceptedCallId => $"{RunId:N}:{BlockId}:{InvocationId}:{CapabilityId}";
}

public static class AgentCapabilityAcceptanceExtensions
{
    public static AgentCapability<TState, TRequest> WithAcceptance<TState, TRequest>(
        this AgentCapability<TState, TRequest> capability,
        Func<
            AgentCapabilityAcceptanceContext<TState, TRequest>,
            CancellationToken,
            ValueTask
        > accept
    )
        where TRequest : class
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(accept);
        return capability.WithAcceptance(
            (context, cancellationToken) =>
                accept(
                    new AgentCapabilityAcceptanceContext<TState, TRequest>(
                        context.RunId,
                        context.BlockId,
                        context.InvocationId,
                        context.CapabilityId,
                        context.State,
                        context.Request
                    ),
                    cancellationToken
                )
        );
    }
}
