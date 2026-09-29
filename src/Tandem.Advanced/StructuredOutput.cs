using System.Text.Json;
using Tandem.Domain;

namespace Tandem.Advanced;

public sealed record StructuredOutputProblem(string Field, string Message);

public sealed record StructuredOutcome<TState>(
    string Kind,
    string Summary,
    JsonElement Payload,
    TState? UpdatedState = default
);

public sealed record StructuredOutputResult<TState>(
    StructuredOutcome<TState>? Outcome,
    IReadOnlyList<StructuredOutputProblem> Problems,
    string RawResponse,
    object? Candidate = null
)
{
    public bool Success => Outcome is not null;
}

public delegate StructuredOutputResult<TState> StructuredOutputParser<TState>(
    string assistantText,
    TState state
);

public sealed record StructuredOutputAcceptanceObservation<TState>(
    AgentMessageContext<TState> Context,
    StructuredOutputResult<TState> Result,
    IReadOnlySet<string> ToolNames,
    int Attempt
);

public delegate IReadOnlyList<StructuredOutputProblem> StructuredOutputAcceptancePolicy<TState>(
    StructuredOutputAcceptanceObservation<TState> observation
);

public sealed record OutputAcceptanceObservation<TState, TOutput>(
    AgentMessageContext<TState> Context,
    string AcceptedOutputId,
    TOutput Output,
    IReadOnlySet<ToolObservation> Tools,
    IReadOnlyList<ToolInvocationObservation> ToolInvocations,
    int Attempt
);

// Output acceptance receives model-authored arguments and process output. Policies should
// avoid persisting observations that may contain credentials or other sensitive values.

public delegate IReadOnlyList<StructuredOutputProblem> OutputAcceptancePolicy<TState, TOutput>(
    OutputAcceptanceObservation<TState, TOutput> observation
);

public delegate ValueTask OutputAcceptance<TState, TOutput>(
    OutputAcceptanceObservation<TState, TOutput> observation,
    CancellationToken cancellationToken
);

internal static class StructuredOutputDescriptors
{
    public static AgentStructuredOutputDescriptor<TState> Create<TState>(
        StructuredOutputParser<TState> parser,
        StructuredOutputAcceptancePolicy<TState>? acceptancePolicy = null,
        string? correctionRequiredToolName = null
    ) =>
        new(
            (response, state) => ToCore(parser(response, state)),
            Accept: acceptancePolicy is null
                ? null
                : (message, result, tools, _, _, attempt) =>
                    acceptancePolicy(
                            new StructuredOutputAcceptanceObservation<TState>(
                                AgentMessageContext<TState>.From(message),
                                ToPublic(result),
                                tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal),
                                attempt
                            )
                        )
                        .Select(problem => new AgentStructuredOutputProblem(
                            problem.Field,
                            problem.Message
                        ))
                        .ToArray(),
            CorrectionRequiredToolName: correctionRequiredToolName
        );

    public static Func<
        PipelineMessage<TState>,
        AgentStructuredOutputResult<TState>,
        IReadOnlySet<Infrastructure.ToolObservationDescriptor>,
        IReadOnlyList<Infrastructure.ToolInvocationObservationDescriptor>,
        string,
        int,
        IReadOnlyList<AgentStructuredOutputProblem>
    > Accept<TState, TOutput>(OutputAcceptancePolicy<TState, TOutput> acceptance) =>
        (message, result, tools, toolInvocations, acceptedOutputId, attempt) =>
            result.Candidate is null ? []
            : result.Candidate is not TOutput output
                ?
                [
                    new AgentStructuredOutputProblem(
                        "$acceptance",
                        $"Configured acceptance expected '{typeof(TOutput).Name}' but received "
                            + $"'{result.Candidate.GetType().Name}'."
                    ),
                ]
            : acceptance(
                    new OutputAcceptanceObservation<TState, TOutput>(
                        AgentMessageContext<TState>.From(message),
                        acceptedOutputId,
                        output,
                        tools.Select(ToPublic).ToHashSet(),
                        toolInvocations.Select(ToPublic).ToArray(),
                        attempt
                    )
                )
                .Select(problem => new AgentStructuredOutputProblem(problem.Field, problem.Message))
                .ToArray();

    public static Func<
        PipelineMessage<TState>,
        AgentStructuredOutputResult<TState>,
        IReadOnlySet<Infrastructure.ToolObservationDescriptor>,
        IReadOnlyList<Infrastructure.ToolInvocationObservationDescriptor>,
        string,
        int,
        CancellationToken,
        ValueTask
    > AcceptAsync<TState, TOutput>(OutputAcceptance<TState, TOutput> acceptance) =>
        (message, result, tools, toolInvocations, acceptedOutputId, attempt, cancellationToken) =>
            result.Candidate is TOutput output
                ? acceptance(
                    new OutputAcceptanceObservation<TState, TOutput>(
                        AgentMessageContext<TState>.From(message),
                        acceptedOutputId,
                        output,
                        tools.Select(ToPublic).ToHashSet(),
                        toolInvocations.Select(ToPublic).ToArray(),
                        attempt
                    ),
                    cancellationToken
                )
                : ValueTask.FromException(
                    new InvalidOperationException(
                        $"Configured acceptance expected '{typeof(TOutput).Name}' but received "
                            + $"'{result.Candidate?.GetType().Name ?? "null"}'."
                    )
                );

    private static AgentStructuredOutputResult<TState> ToCore<TState>(
        StructuredOutputResult<TState> result
    ) =>
        new(
            result.Outcome is { } outcome
                ? new AgentStructuredOutcome<TState>(
                    outcome.Kind,
                    outcome.Summary,
                    outcome.Payload,
                    outcome.UpdatedState
                )
                : null,
            result
                .Problems.Select(problem => new AgentStructuredOutputProblem(
                    problem.Field,
                    problem.Message
                ))
                .ToArray(),
            result.RawResponse,
            result.Candidate
        );

    private static StructuredOutputResult<TState> ToPublic<TState>(
        AgentStructuredOutputResult<TState> result
    ) =>
        new(
            result.Outcome is { } outcome
                ? new StructuredOutcome<TState>(
                    outcome.Kind,
                    outcome.Summary,
                    outcome.Payload,
                    outcome.UpdatedState
                )
                : null,
            result
                .Problems.Select(problem => new StructuredOutputProblem(
                    problem.Field,
                    problem.Message
                ))
                .ToArray(),
            result.RawResponse,
            result.Candidate
        );

    private static ToolObservation ToPublic(Infrastructure.ToolObservationDescriptor observation) =>
        new(
            observation.Name,
            observation.Semantics?.Effect switch
            {
                Infrastructure.ToolEffect.Read => ToolEffect.Read,
                Infrastructure.ToolEffect.WorkspaceMutation => ToolEffect.WorkspaceMutation,
                Infrastructure.ToolEffect.ProcessExecution => ToolEffect.ProcessExecution,
                Infrastructure.ToolEffect.LifecycleTransition => ToolEffect.LifecycleTransition,
                _ => ToolEffect.Unclassified,
            },
            observation.Semantics?.Evidence == Infrastructure.ToolEvidence.RepositoryInspection
                ? ToolEvidence.RepositoryInspection
                : ToolEvidence.None
        );

    internal static ToolInvocationObservation ToPublic(
        Infrastructure.ToolInvocationObservationDescriptor observation
    ) =>
        new(
            observation.Name,
            ToPublic(observation.Semantics?.Effect),
            observation.Arguments.Clone(),
            observation.Status switch
            {
                Infrastructure.ToolInvocationStatus.Completed => ToolInvocationStatus.Completed,
                Infrastructure.ToolInvocationStatus.Failed => ToolInvocationStatus.Failed,
                Infrastructure.ToolInvocationStatus.Blocked => ToolInvocationStatus.Blocked,
                _ => ToolInvocationStatus.Faulted,
            },
            observation.Result is Infrastructure.ToolResultEvidenceDescriptor.Process process
                ? new ToolResultEvidence.Process(
                    process.ExitCode,
                    process.Stdout,
                    process.Stderr,
                    process.Duration,
                    process.TimedOut,
                    process.Truncated
                )
                : null
        );

    private static ToolEffect ToPublic(Infrastructure.ToolEffect? effect) =>
        effect switch
        {
            Infrastructure.ToolEffect.Read => ToolEffect.Read,
            Infrastructure.ToolEffect.WorkspaceMutation => ToolEffect.WorkspaceMutation,
            Infrastructure.ToolEffect.ProcessExecution => ToolEffect.ProcessExecution,
            Infrastructure.ToolEffect.LifecycleTransition => ToolEffect.LifecycleTransition,
            _ => ToolEffect.Unclassified,
        };
}
