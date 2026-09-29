using Tandem.Domain;

namespace Tandem.Advanced;

public sealed record OutputAcceptanceObservation<TState, TOutput>(
    AgentMessageContext<TState> Context,
    string AcceptedOutputId,
    TOutput Output,
    IReadOnlySet<ToolObservation> Tools,
    IReadOnlyList<ToolInvocationObservation> ToolInvocations,
    int Attempt
)
{
    internal static OutputAcceptanceObservation<TState, TOutput> From(
        StructuredOutputAttempt<TState> attempt,
        TOutput output
    ) =>
        new(
            AgentMessageContext<TState>.From(attempt.Message),
            attempt.AcceptedOutputId,
            output,
            attempt.Tools,
            attempt.ToolInvocations,
            attempt.Attempt
        );
}

// Output acceptance receives model-authored arguments and process output. Policies should
// avoid persisting observations that may contain credentials or other sensitive values.

public delegate IReadOnlyList<ValidationProblem> OutputAcceptancePolicy<TState, TOutput>(
    OutputAcceptanceObservation<TState, TOutput> observation
);

public delegate ValueTask OutputAcceptance<TState, TOutput>(
    OutputAcceptanceObservation<TState, TOutput> observation,
    CancellationToken cancellationToken
);
