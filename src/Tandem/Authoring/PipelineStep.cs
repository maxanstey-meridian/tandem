using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IGeneratedPipelineStep<TState, TResult>
    : IPipelineNode<TState>,
        IPipelineStep<TResult>;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IStandardOutcomePipelineStep<TState>
    : IGeneratedPipelineStep<TState, Outcome<TState>>;

[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct GeneratedStepCompletion;

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedPassThroughStepDescriptor<TState>(
    string id,
    Func<TState, CancellationToken, ValueTask> execute
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() =>
        new GeneratedStepExecutor<TState>(
            id,
            async (state, cancellationToken) =>
            {
                await execute(state, cancellationToken);
                return new Outcome<TState>.Success(
                    PipelineExecutionEnvelope.CurrentState<TState>()
                );
            },
            new(),
            _ => null
        ).BindExecutor();
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedStateStepDescriptor<TState>(
    string id,
    Func<TState, CancellationToken, ValueTask<TState>> execute
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() =>
        new GeneratedStepExecutor<TState>(
            id,
            async (state, cancellationToken) =>
                new Outcome<TState>.Success(await execute(state, cancellationToken)),
            new(),
            output => PipelineAcceptedValue.From(output.State)
        ).BindExecutor();
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedOutcomeStepDescriptor<TState>(
    string id,
    Func<TState, CancellationToken, ValueTask<Outcome<TState>>> execute
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() => Bind(new StandardOutcomeRouteAwareness<TState>());

    internal ValueTask<PipelineMessage<TState>> ExecuteScopedAsync(
        PipelineMessage<TState> message,
        CancellationToken token
    ) => Create(new()).ExecuteAsync(message, token);

    internal ExecutorBinding Bind(StandardOutcomeRouteAwareness<TState> routeAwareness) =>
        Create(routeAwareness).BindExecutor();

    private GeneratedStepExecutor<TState> Create(
        StandardOutcomeRouteAwareness<TState> routeAwareness
    ) =>
        new(
            id,
            execute,
            routeAwareness,
            output =>
                output.LatestOutcome?.Kind == StandardOutcomeKinds.Success
                    ? PipelineAcceptedValue.From(output.State)
                    : null
        );
}

internal sealed class StandardOutcomeRouteAwareness<TState>
{
    public Func<PipelineMessage<TState>, bool> Matches { get; set; } = _ => false;
}

public readonly struct PipelineOutcomeSelector<TState>
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PipelineOutcomeSelector(IStandardOutcomePipelineStep<TState> source, bool failed)
    {
        Source = source;
        Failed = failed;
    }

    internal IStandardOutcomePipelineStep<TState> Source { get; }
    internal bool Failed { get; }
    internal string CaseId =>
        Failed ? nameof(Outcome<TState>.Failed) : nameof(Outcome<TState>.Success);
}

internal sealed class GeneratedStepExecutor<TState>(
    string id,
    Func<TState, CancellationToken, ValueTask<Outcome<TState>>> execute,
    StandardOutcomeRouteAwareness<TState> routeAwareness,
    Func<PipelineMessage<TState>, PipelineAcceptedValue?> acceptedValue
)
    : Executor<PipelineMessage<TState>, PipelineMessage<TState>>(
        id,
        options: null,
        declareCrossRunShareable: true
    )
{
    public override async ValueTask<PipelineMessage<TState>> HandleAsync(
        PipelineMessage<TState> pipeline,
        IWorkflowContext context,
        CancellationToken cancellationToken
    ) => await ExecuteAsync(pipeline, cancellationToken);

    internal async ValueTask<PipelineMessage<TState>> ExecuteAsync(
        PipelineMessage<TState> pipeline,
        CancellationToken cancellationToken
    )
    {
        using var lease = await ParallelBranchLease.EnterAsync(
            pipeline.ParallelContext?.Slots,
            cancellationToken
        );
        using var envelope = PipelineExecutionEnvelope.Begin(pipeline);
        return await PipelineObservationPublisher.ExecuteAsync(
            Id,
            PipelineObservationMode.Full,
            pipeline,
            async () =>
                await execute(pipeline.State, cancellationToken) switch
                {
                    Outcome<TState>.Success success => StandardOutcomes.Succeeded(
                        envelope.Message with
                        {
                            State = success.State,
                        },
                        Id
                    ),
                    Outcome<TState>.Failed failed => StandardOutcomes.Failed(
                        envelope.Message with
                        {
                            State = failed.State,
                        },
                        Id,
                        failed.Failure.Summary,
                        JsonSerializer.SerializeToElement(failed.Failure),
                        routeAwareness
                    ),
                    _ => throw new InvalidOperationException("Unknown standard outcome."),
                },
            cancellationToken,
            acceptedValue
        );
    }
}

internal static class StandardOutcomes
{
    public static PipelineMessage<TState> Succeeded<TState>(
        PipelineMessage<TState> message,
        string stepId
    ) =>
        message with
        {
            LatestOutcome = new BlockOutcome(
                StandardOutcomeKinds.Success,
                stepId,
                "Succeeded",
                TandemJson.EmptyObject
            ),
            LatestResult = PipelineResultPayload.Create(
                stepId,
                nameof(Outcome<TState>.Success),
                new { }
            ),
        };

    public static PipelineMessage<TState> Failed<TState>(
        PipelineMessage<TState> message,
        string stepId,
        string summary,
        JsonElement evidence,
        StandardOutcomeRouteAwareness<TState> routeAwareness
    )
    {
        var failed = message with
        {
            LatestOutcome = new BlockOutcome(
                StandardOutcomeKinds.Failed,
                stepId,
                summary,
                evidence
            ),
            LatestResult = PipelineResultPayload.Create(
                stepId,
                nameof(Outcome<TState>.Failed),
                evidence
            ),
            Status = PipelineRunStatus.Succeeded,
        };
        return routeAwareness.Matches(failed)
            ? failed
            : failed with
            {
                Status = PipelineRunStatus.Failed,
            };
    }
}

internal static class PipelineExecutionEnvelope
{
    private static readonly AsyncLocal<IScope?> _current = new();

    public static PipelineExecutionScope<TState> Begin<TState>(PipelineMessage<TState> message)
    {
        var scope = new PipelineExecutionScope<TState>(_current.Value, message);
        _current.Value = scope;
        return scope;
    }

    public static void Set<TState>(PipelineMessage<TState> message)
    {
        if (_current.Value is not PipelineExecutionScope<TState> scope)
        {
            throw new InvalidOperationException(
                "Agent operations can only update their active generated pipeline step."
            );
        }
        scope.Message = message;
    }

    public static IDisposable BeginOperation<TState>()
    {
        if (_current.Value is not PipelineExecutionScope<TState> scope)
        {
            throw new InvalidOperationException(
                "Operations can only run while an active generated pipeline step is executing."
            );
        }
        if (!scope.TryEnterOperation())
        {
            throw new InvalidOperationException(
                "Concurrent sibling operations cannot run within the same generated pipeline step. Await the active operation before starting another."
            );
        }
        return new OperationLease<TState>(scope);
    }

    public static TState CurrentState<TState>() =>
        _current.Value is PipelineExecutionScope<TState> scope
            ? scope.Message.State
            : throw new InvalidOperationException(
                "Operations can only run while a generated pipeline step is executing."
            );

    public static PipelineMessage<TState> Get<TState>(TState state)
    {
        if (_current.Value is not PipelineExecutionScope<TState> scope)
        {
            throw new InvalidOperationException(
                "Operations can only run while a generated pipeline step is executing."
            );
        }
        return scope.Message with { State = state };
    }

    internal interface IScope
    {
        public string? VisitId { get; }
    }

    internal static string? VisitId => _current.Value?.VisitId;

    internal sealed class PipelineExecutionScope<TState>(
        IScope? parent,
        PipelineMessage<TState> message
    ) : IDisposable, IScope
    {
        private bool _disposed;
        private int _operationActive;

        public PipelineMessage<TState> Message { get; set; } = message;
        public string? VisitId => Message.Runtime.ObservationVisitId;

        public bool TryEnterOperation() =>
            Interlocked.CompareExchange(ref _operationActive, 1, 0) == 0;

        public void ExitOperation() => Volatile.Write(ref _operationActive, 0);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _current.Value = parent;
        }
    }

    private sealed class OperationLease<TState>(PipelineExecutionScope<TState> scope) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                scope.ExitOperation();
            }
        }
    }
}

internal static class PipelineResultPayload
{
    public static PipelineResult Create<TCase>(string stepId, string caseId, TCase value) =>
        new(stepId, caseId, JsonSerializer.SerializeToElement(value));
}
