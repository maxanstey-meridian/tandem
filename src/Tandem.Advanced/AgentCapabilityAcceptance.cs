namespace Tandem.Advanced;

public sealed record AgentCapabilityAcceptanceContext<TState, TRequest>(
    Guid RunId,
    string StepId,
    string InvocationId,
    string CapabilityId,
    string AcceptedCallId,
    TState State,
    TRequest Request
);

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
                        context.StepId,
                        context.InvocationId,
                        context.CapabilityId,
                        context.AcceptedCallId,
                        context.State,
                        context.Request
                    ),
                    cancellationToken
                )
        );
    }
}
