using System.Text.Json;

namespace Tandem.Advanced;

public sealed record AgentCapabilityAcceptanceContext<TState, TRequest>(
    Guid RunId,
    string StepId,
    string InvocationId,
    string CapabilityId,
    string AcceptedCallId,
    TState State,
    TRequest Request
)
{
    public IReadOnlyList<ToolInvocationObservation> ToolInvocations { get; init; } = [];
}

public static class AgentCapabilityAcceptanceExtensions
{
    public static AgentCapability<TState> WithAcceptance<TState>(
        this AgentCapability<TState> capability,
        Func<
            AgentCapabilityAcceptanceContext<TState, JsonElement>,
            CancellationToken,
            ValueTask
        > accept
    )
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(accept);
        return capability.WithJsonAcceptance(
            (context, cancellationToken) =>
                accept(
                    new AgentCapabilityAcceptanceContext<TState, JsonElement>(
                        context.RunId,
                        context.StepId,
                        context.InvocationId,
                        context.CapabilityId,
                        context.AcceptedCallId,
                        context.State,
                        context.Request
                    )
                    {
                        ToolInvocations = context
                            .ToolInvocations.Select(StructuredOutputDescriptors.ToPublic)
                            .ToArray(),
                    },
                    cancellationToken
                )
        );
    }

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
                    )
                    {
                        ToolInvocations = context
                            .ToolInvocations.Select(StructuredOutputDescriptors.ToPublic)
                            .ToArray(),
                    },
                    cancellationToken
                )
        );
    }
}
