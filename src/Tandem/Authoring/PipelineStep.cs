using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Tandem.Advanced;
using Tandem.Domain;
using Tandem.Infrastructure.Projection;

namespace Tandem;

public interface IPipelineNode
{
    public string Id { get; }

    // Public only because source-generated consumer classes compile in a separate assembly.
    // This is an opaque generated-code SPI, not an authoring extension point.
    [EditorBrowsable(EditorBrowsableState.Never)]
    public PipelineNodeDescriptor Descriptor { get; }
}

public interface IPipelineNode<TState> : IPipelineNode;

internal interface IRawPipelineNode : IPipelineNode;

public interface IPipelineStep<TResult> : IPipelineNode;

[EditorBrowsable(EditorBrowsableState.Never)]
public abstract class PipelineNodeDescriptor
{
    internal abstract ExecutorBinding Bind();
}

public static class PipelineNodes
{
    public static IPipelineNode<TState> Failed<TState>(string id) =>
        new TerminalFailedNode<TState>(id);

    public static IPipelineNode<TState> Failed<TState>(
        string id,
        Func<TState, TState> transition,
        string outcomeKind,
        Func<string, string, string> summarize,
        IBlockExecutionObserver? observer = null
    ) => new StateTransitionFailedNode<TState>(id, transition, outcomeKind, summarize, observer);

    public static IPipelineNode<TState> Complete<TState>(string id) =>
        new TerminalCompleteNode<TState>(id);

    public static IPipelineNode<TState> Complete<TState>(
        string id,
        Func<TState, TState> transition,
        string outcomeKind,
        string summary,
        IBlockExecutionObserver? observer = null
    ) => new StateTransitionCompleteNode<TState>(id, transition, outcomeKind, summary, observer);

    public static PipelineInteraction<TState, TRequest, TResponse> WaitFor<
        TState,
        TRequest,
        TResponse
    >(
        string id,
        Func<TState, TRequest> createRequest,
        Func<TState, TResponse, TState> applyResponse,
        IBlockExecutionObserver? observer = null
    ) => new(id, createRequest, applyResponse, observer);
}

internal sealed class TerminalCompleteNode<TState>(string id)
    : IPipelineNode<TState>,
        IRawPipelineNode
{
    public string Id => id;

    public PipelineNodeDescriptor Descriptor { get; } =
        AdvancedPipelineNodes.Stage<PipelineMessage<TState>, PipelineMessage<TState>>(
            id,
            (message, _, _) =>
                ValueTask.FromResult(
                    message with
                    {
                        LatestOutcome = new BlockOutcome(
                            StandardOutcomeKinds.Success,
                            id,
                            "Succeeded",
                            JsonSerializer.SerializeToElement(new { })
                        ),
                        LatestResult = PipelineResultPayload.Create(
                            id,
                            nameof(Outcome<object>.Success),
                            message.State
                        ),
                    }
                )
        );
}

internal sealed class TerminalFailedNode<TState>(string id)
    : IPipelineNode<TState>,
        IRawPipelineNode
{
    public string Id => id;

    public PipelineNodeDescriptor Descriptor { get; } =
        AdvancedPipelineNodes.Stage<PipelineMessage<TState>, PipelineMessage<TState>>(
            id,
            (message, _, _) =>
                ValueTask.FromResult(message with { Disposition = PipelineRunDisposition.Failed })
        );
}

internal sealed class StateTransitionCompleteNode<TState>(
    string id,
    Func<TState, TState> transition,
    string outcomeKind,
    string summary,
    IBlockExecutionObserver? observer
) : IPipelineNode<TState>
{
    public string Id => id;

    public PipelineNodeDescriptor Descriptor { get; } =
        new DelegatePipelineNodeDescriptor<PipelineMessage<TState>, PipelineMessage<TState>>(
            id,
            (message, _, _) =>
            {
                var stopwatch = Stopwatch.StartNew();
                var state = transition(message.State);
                stopwatch.Stop();
                return ValueTask.FromResult(
                    new PipelineMessage<TState>(
                        message.Runtime,
                        state,
                        new BlockOutcome(
                            outcomeKind,
                            id,
                            summary,
                            JsonSerializer.SerializeToElement(new { }),
                            stopwatch.Elapsed
                        )
                    )
                );
            },
            observer
        );
}

internal sealed class StateTransitionFailedNode<TState>(
    string id,
    Func<TState, TState> transition,
    string outcomeKind,
    Func<string, string, string> summarize,
    IBlockExecutionObserver? observer
) : IPipelineNode<TState>
{
    public string Id => id;

    public PipelineNodeDescriptor Descriptor { get; } =
        new DelegatePipelineNodeDescriptor<PipelineMessage<TState>, PipelineMessage<TState>>(
            id,
            (message, _, _) =>
            {
                var sourceBlock = message.LatestOutcome?.BlockId ?? "unknown";
                var sourceKind = message.LatestOutcome?.Kind ?? "unknown";
                var stopwatch = Stopwatch.StartNew();
                var state = transition(message.State);
                stopwatch.Stop();
                return ValueTask.FromResult(
                    message with
                    {
                        State = state,
                        LatestOutcome = new BlockOutcome(
                            outcomeKind,
                            id,
                            summarize(sourceBlock, sourceKind),
                            message.LatestOutcome?.Payload
                                ?? JsonSerializer.SerializeToElement(new { }),
                            stopwatch.Elapsed
                        ),
                        Disposition = PipelineRunDisposition.Failed,
                    }
                );
            },
            observer
        );
}

internal sealed class DelegatePipelineNodeDescriptor<TInput, TOutput>(
    string id,
    Func<TInput, IPipelineExecutionContext, CancellationToken, ValueTask<TOutput>> execute,
    IBlockExecutionObserver? observer
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind()
    {
        var executor = new DelegatePipelineNodeExecutor<TInput, TOutput>(id, execute);
        return observer is null
            ? executor.BindExecutor()
            : new ObservedExecutor<TInput, TOutput>(id, executor, observer).BindExecutor();
    }
}

internal sealed class RequestPortPipelineNodeDescriptor<TRequest, TResponse>(string id)
    : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() =>
        (ExecutorBinding)RequestPort.Create<TRequest, TResponse>(id);
}

internal sealed class DelegatePipelineNodeExecutor<TInput, TOutput>(
    string id,
    Func<TInput, IPipelineExecutionContext, CancellationToken, ValueTask<TOutput>> execute
) : Executor<TInput, TOutput>(id)
{
    public override ValueTask<TOutput> HandleAsync(
        TInput input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    ) => execute(input, new PipelineExecutionContext(context), cancellationToken);
}

internal sealed class PipelineExecutionContext(IWorkflowContext context) : IPipelineExecutionContext
{
    public ValueTask QueueStateUpdateAsync(
        string key,
        string value,
        string scopeName,
        CancellationToken cancellationToken
    ) => context.QueueStateUpdateAsync(key, value, scopeName, cancellationToken);

    public ValueTask<HashSet<string>> ReadStateKeysAsync(
        string scopeName,
        CancellationToken cancellationToken
    ) => context.ReadStateKeysAsync(scopeName, cancellationToken);

    public ValueTask<T?> ReadStateAsync<T>(
        string key,
        string scopeName,
        CancellationToken cancellationToken
    ) => context.ReadStateAsync<T>(key, scopeName, cancellationToken);
}

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IGeneratedPipelineStep<TState, TResult> : IPipelineStep<TResult>;

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IStandardOutcomePipelineStep<TState>
    : IGeneratedPipelineStep<TState, Outcome<TState>>;

[EditorBrowsable(EditorBrowsableState.Never)]
public readonly struct GeneratedStepCompletion;

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedPipelineStepDescriptor<TState, TResult>(
    string id,
    Func<PipelineMessage<TState>, CancellationToken, ValueTask<TResult>> execute,
    Func<PipelineMessage<TState>, TResult, PipelineMessage<TState>> adapt
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() =>
        new GeneratedStepExecutor<TState, TResult>(id, execute, adapt).Bind();
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedPassThroughStepDescriptor<TState>(
    string id,
    Func<TState, CancellationToken, ValueTask> execute
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() =>
        new GeneratedPassThroughStepExecutor<TState>(id, execute).Bind();
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedStateStepDescriptor<TState>(
    string id,
    Func<TState, CancellationToken, ValueTask<TState>> execute
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() =>
        new GeneratedStateStepExecutor<TState>(id, execute).Bind();
}

[EditorBrowsable(EditorBrowsableState.Never)]
public sealed class GeneratedOutcomeStepDescriptor<TState>(
    string id,
    Func<TState, CancellationToken, ValueTask<Outcome<TState>>> execute
) : PipelineNodeDescriptor
{
    private readonly StandardOutcomeRouteAwareness<TState> _routeAwareness = new();

    internal override ExecutorBinding Bind() =>
        new GeneratedOutcomeStepExecutor<TState>(id, execute, _routeAwareness).Bind();

    internal void SetFailureRouteMatcher(Func<PipelineMessage<TState>, bool> matcher) =>
        _routeAwareness.Matches = matcher;
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

internal sealed class GeneratedStepExecutor<TState, TResult>(
    string id,
    Func<PipelineMessage<TState>, CancellationToken, ValueTask<TResult>> execute,
    Func<PipelineMessage<TState>, TResult, PipelineMessage<TState>> adapt
) : Executor<PipelineMessage<TState>, PipelineMessage<TState>>(id)
{
    internal ExecutorBinding Bind() => this.BindExecutor();

    public override async ValueTask<PipelineMessage<TState>> HandleAsync(
        PipelineMessage<TState> pipeline,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        using var envelope = PipelineExecutionEnvelope.Begin(pipeline);
        var result = await execute(pipeline, cancellationToken);
        return adapt(envelope.Message, result);
    }
}

internal sealed class GeneratedPassThroughStepExecutor<TState>
    : Executor<PipelineMessage<TState>, PipelineMessage<TState>>
{
    private readonly string _id;
    private readonly Func<TState, CancellationToken, ValueTask> _execute;

    public GeneratedPassThroughStepExecutor(
        string id,
        Func<TState, CancellationToken, ValueTask> execute
    )
        : base(id)
    {
        _id = id;
        _execute = execute;
    }

    internal ExecutorBinding Bind() => this.BindExecutor();

    public override async ValueTask<PipelineMessage<TState>> HandleAsync(
        PipelineMessage<TState> pipeline,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        using var envelope = PipelineExecutionEnvelope.Begin(pipeline);
        await _execute(pipeline.State, cancellationToken);
        return envelope.Message with
        {
            LatestOutcome = new BlockOutcome(
                StandardOutcomeKinds.Success,
                _id,
                "Succeeded",
                JsonSerializer.SerializeToElement(new { })
            ),
            LatestResult = PipelineResultPayload.Create(
                _id,
                nameof(Outcome<object>.Success),
                new { }
            ),
        };
    }
}

internal sealed class GeneratedStateStepExecutor<TState>
    : Executor<PipelineMessage<TState>, PipelineMessage<TState>>
{
    private readonly string _id;
    private readonly Func<TState, CancellationToken, ValueTask<TState>> _execute;

    public GeneratedStateStepExecutor(
        string id,
        Func<TState, CancellationToken, ValueTask<TState>> execute
    )
        : base(id)
    {
        _id = id;
        _execute = execute;
    }

    internal ExecutorBinding Bind() => this.BindExecutor();

    public override async ValueTask<PipelineMessage<TState>> HandleAsync(
        PipelineMessage<TState> pipeline,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        using var envelope = PipelineExecutionEnvelope.Begin(pipeline);
        var state = await _execute(pipeline.State, cancellationToken);
        return envelope.Message with
        {
            State = state,
            LatestOutcome = new BlockOutcome(
                StandardOutcomeKinds.Success,
                _id,
                "Succeeded",
                JsonSerializer.SerializeToElement(new { })
            ),
            LatestResult = PipelineResultPayload.Create(
                _id,
                nameof(Outcome<object>.Success),
                state
            ),
        };
    }
}

internal sealed class GeneratedOutcomeStepExecutor<TState>
    : Executor<PipelineMessage<TState>, PipelineMessage<TState>>
{
    private readonly string _id;
    private readonly Func<TState, CancellationToken, ValueTask<Outcome<TState>>> _execute;
    private readonly StandardOutcomeRouteAwareness<TState> _routeAwareness;

    public GeneratedOutcomeStepExecutor(
        string id,
        Func<TState, CancellationToken, ValueTask<Outcome<TState>>> execute,
        StandardOutcomeRouteAwareness<TState> routeAwareness
    )
        : base(id)
    {
        _id = id;
        _execute = execute;
        _routeAwareness = routeAwareness;
    }

    internal ExecutorBinding Bind() => this.BindExecutor();

    public override async ValueTask<PipelineMessage<TState>> HandleAsync(
        PipelineMessage<TState> pipeline,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        using var envelope = PipelineExecutionEnvelope.Begin(pipeline);
        var result = await _execute(pipeline.State, cancellationToken);
        return result switch
        {
            Outcome<TState>.Success success => envelope.Message with
            {
                State = success.State,
                LatestOutcome = new BlockOutcome(
                    StandardOutcomeKinds.Success,
                    _id,
                    "Succeeded",
                    JsonSerializer.SerializeToElement(new { })
                ),
                LatestResult = PipelineResultPayload.Create(
                    _id,
                    nameof(Outcome<TState>.Success),
                    success
                ),
            },
            Outcome<TState>.Failed failed => AdaptFailed(envelope.Message, failed),
            _ => throw new InvalidOperationException("Unknown standard outcome."),
        };
    }

    private PipelineMessage<TState> AdaptFailed(
        PipelineMessage<TState> pipeline,
        Outcome<TState>.Failed failed
    )
    {
        var result = pipeline with
        {
            State = failed.State,
            LatestOutcome = new BlockOutcome(
                StandardOutcomeKinds.Failed,
                _id,
                failed.Failure.Summary,
                JsonSerializer.SerializeToElement(failed.Failure)
            ),
            LatestResult = PipelineResultPayload.Create(
                _id,
                nameof(Outcome<TState>.Failed),
                failed
            ),
            Disposition = null,
        };
        return result with
        {
            Disposition = _routeAwareness.Matches(result) ? null : PipelineRunDisposition.Failed,
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

    internal interface IScope;

    internal sealed class PipelineExecutionScope<TState>(
        IScope? parent,
        PipelineMessage<TState> message
    ) : IDisposable, IScope
    {
        private bool _disposed;
        private int _operationActive;

        public PipelineMessage<TState> Message { get; set; } = message;

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

public sealed class Pipeline
{
    private readonly IReadOnlyList<string> _outputStepIds;

    internal Pipeline(Workflow workflow, IReadOnlyList<string> outputStepIds)
    {
        Workflow = workflow;
        _outputStepIds = outputStepIds;
    }

    internal Workflow Workflow { get; }

    public PipelineInspection Inspect()
    {
        var physicalStepIds = Workflow.ReflectExecutors().Keys.ToArray();
        var interactionIds = Workflow
            .ReflectPorts()
            .Keys.Where(id =>
                physicalStepIds.Contains($"{id}--request", StringComparer.Ordinal)
                && physicalStepIds.Contains($"{id}--resume", StringComparer.Ordinal)
            )
            .ToHashSet(StringComparer.Ordinal);
        string SemanticId(string id)
        {
            foreach (var interactionId in interactionIds)
            {
                if (
                    id == interactionId
                    || id == $"{interactionId}--request"
                    || id == $"{interactionId}--resume"
                )
                {
                    return interactionId;
                }
            }
            return id;
        }

        var routes = Workflow
            .ReflectEdges()
            .SelectMany(entry =>
                entry.Value.Select(edge => new PipelineRouteInspection(
                    SemanticId(edge.Connection.SourceIds.Single()),
                    SemanticId(edge.Connection.SinkIds.Single()),
                    edge is DirectEdgeInfo direct && direct.HasCondition
                ))
            )
            .Where(route => route.SourceId != route.TargetId)
            .ToArray();
        var ports = Workflow
            .ReflectPorts()
            .Where(entry => !interactionIds.Contains(entry.Key))
            .Select(entry => entry.Value)
            .Select(port => new PipelinePortInspection(
                port.PortId,
                port.RequestType.TypeName,
                port.ResponseType.TypeName
            ))
            .OrderBy(port => port.Id, StringComparer.Ordinal)
            .ToArray();
        return new PipelineInspection(
            Workflow.Name ?? throw new InvalidOperationException("Pipeline name is unavailable."),
            Workflow.Description,
            Workflow.StartExecutorId,
            physicalStepIds
                .Select(SemanticId)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            ports,
            routes
                .OrderBy(route => route.SourceId, StringComparer.Ordinal)
                .ThenBy(route => route.TargetId, StringComparer.Ordinal)
                .ThenBy(route => route.Conditional)
                .ToArray(),
            _outputStepIds,
            WorkflowVisualizer.ToMermaidString(Workflow),
            WorkflowVisualizer.ToDotString(Workflow)
        );
    }
}

public sealed record PipelineInspection(
    string Name,
    string? Description,
    string StartStepId,
    IReadOnlyList<string> StepIds,
    IReadOnlyList<PipelinePortInspection> Ports,
    IReadOnlyList<PipelineRouteInspection> Routes,
    IReadOnlyList<string> OutputStepIds,
    string Mermaid,
    string Dot
);

public sealed record PipelineRouteInspection(string SourceId, string TargetId, bool Conditional);

public sealed record PipelinePortInspection(string Id, string InputType, string OutputType);

internal static class PipelineMafBridge
{
    public static Workflow GetWorkflow(Pipeline pipeline) => pipeline.Workflow;
}

public static class TandemWorkflow
{
    public static PipelineBuilder<TState> Start<TState, TResult>(
        IGeneratedPipelineStep<TState, TResult> at,
        string name,
        string? description = null
    ) => PipelineBuilder<TState>.Create(at, name, description);
}

public sealed class PipelineBuilder<TState>
{
    private readonly WorkflowBuilder _builder;
    private readonly Dictionary<IPipelineNode, ExecutorBinding> _bindings = new(
        PipelineStepReferenceComparer.Instance
    );
    private readonly Dictionary<IPipelineNode, PipelineNodeDescriptor> _descriptors = new(
        PipelineStepReferenceComparer.Instance
    );
    private readonly Dictionary<IPipelineNode, RouteMode> _routeModes = new(
        PipelineStepReferenceComparer.Instance
    );
    private readonly HashSet<object> _interactions = [];
    private readonly Dictionary<
        IPipelineNode,
        List<Func<PipelineMessage<TState>, bool>?>
    > _failureRoutes = new(PipelineStepReferenceComparer.Instance);

    private PipelineBuilder(WorkflowBuilder builder)
    {
        _builder = builder;
    }

    internal static PipelineBuilder<TState> Create<TResult>(
        IGeneratedPipelineStep<TState, TResult> start,
        string name,
        string? description
    )
    {
        var descriptor = start.Descriptor;
        var binding = descriptor.Bind();
        var workflowBuilder = new WorkflowBuilder(binding).WithName(name);
        if (!string.IsNullOrWhiteSpace(description))
        {
            workflowBuilder = workflowBuilder.WithDescription(description);
        }
        var result = new PipelineBuilder<TState>(workflowBuilder);
        result._bindings.Add(start, binding);
        result._descriptors.Add(start, descriptor);
        return result;
    }

    public PipelineBuilder<TState> Route<TTargetResult>(
        PipelineOutcomeSelector<TState> on,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    ) => RouteOutcome(on, when: null, to, label);

    public PipelineBuilder<TState> Route(
        PipelineOutcomeSelector<TState> on,
        IPipelineNode<TState> to,
        string label
    ) => RouteOutcome(on, when: null, to, label);

    public PipelineBuilder<TState> Route<TRequest, TResponse>(
        PipelineOutcomeSelector<TState> on,
        PipelineInteraction<TState, TRequest, TResponse> to,
        string label
    )
    {
        EnsureInteraction(to);
        return RouteOutcome(on, when: null, to.Request, label);
    }

    public PipelineBuilder<TState> Route<TTargetResult>(
        PipelineOutcomeSelector<TState> on,
        Func<TState, bool> when,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    ) => RouteOutcome(on, when, to, label);

    public PipelineBuilder<TState> Route(
        PipelineOutcomeSelector<TState> on,
        Func<TState, bool> when,
        IPipelineNode<TState> to,
        string label
    ) => RouteOutcome(on, when, to, label);

    public PipelineBuilder<TState> Route<TRequest, TResponse>(
        PipelineOutcomeSelector<TState> on,
        Func<TState, bool> when,
        PipelineInteraction<TState, TRequest, TResponse> to,
        string label
    )
    {
        EnsureInteraction(to);
        return RouteOutcome(on, when, to.Request, label);
    }

    public PipelineBuilder<TState> Route<TSourceResult, TTargetResult>(
        IGeneratedPipelineStep<TState, TSourceResult> on,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
    {
        EnsureRouteMode(on, RouteMode.Output);
        TrackFailureRoute(on, when: null);
        _builder.AddEdge(Bind(on), Bind(to), label, idempotent: false);
        return this;
    }

    public PipelineBuilder<TState> Route<TSourceResult, TTargetResult>(
        Func<TState, bool> when,
        IGeneratedPipelineStep<TState, TSourceResult> from,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
    {
        EnsureRouteMode(from, RouteMode.Output);
        TrackFailureRoute(from, pipeline => when(pipeline.State));
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(from),
            Bind(to),
            pipeline => pipeline is not null && when(pipeline.State),
            label,
            idempotent: false
        );
        return this;
    }

    public PipelineBuilder<TState> Route<TSourceResult>(
        Func<TState, bool> when,
        IGeneratedPipelineStep<TState, TSourceResult> from,
        IPipelineNode<TState> to,
        string label
    )
    {
        EnsureRouteMode(from, RouteMode.Output);
        TrackFailureRoute(from, pipeline => when(pipeline.State));
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(from),
            Bind(to),
            pipeline => pipeline is not null && when(pipeline.State),
            label,
            idempotent: false
        );
        return this;
    }

    public PipelineBuilder<TState> Route<TRequest, TResponse>(
        Func<TState, bool> when,
        PipelineInteraction<TState, TRequest, TResponse> from,
        IPipelineNode<TState> to,
        string label
    )
    {
        EnsureInteraction(from);
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(from.Resume),
            Bind(to),
            pipeline => pipeline is not null && when(pipeline.State),
            label,
            idempotent: false
        );
        return this;
    }

    public PipelineBuilder<TState> Route<TRequest, TResponse, TTargetResult>(
        Func<TState, bool> when,
        PipelineInteraction<TState, TRequest, TResponse> from,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
    {
        EnsureInteraction(from);
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(from.Resume),
            Bind(to),
            pipeline => pipeline is not null && when(pipeline.State),
            label,
            idempotent: false
        );
        return this;
    }

    public PipelineBuilder<TState> RouteWithContext<TTargetResult>(
        Func<PipelineMessage<TState>, bool> when,
        IPipelineNode from,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
    {
        EnsureRouteMode(from, RouteMode.Output);
        TrackFailureRoute(from, when);
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(from),
            Bind(to),
            pipeline => pipeline is not null && when(pipeline),
            label,
            idempotent: false
        );
        return this;
    }

    public Pipeline Build(params IPipelineNode[] outputs)
    {
        var outputBindings = outputs.Select(Bind).ToArray();

        foreach (var node in _bindings.Keys)
        {
            if (_descriptors[node] is GeneratedOutcomeStepDescriptor<TState> descriptor)
            {
                var routes = _failureRoutes.GetValueOrDefault(node) ?? [];
                descriptor.SetFailureRouteMatcher(message =>
                    routes.Any(route => route is null || route(message))
                );
            }
        }

        _builder.WithOutputFrom(outputBindings);
        return new Pipeline(_builder.Build(), outputs.Select(output => output.Id).ToArray());
    }

    private PipelineBuilder<TState> RouteOutcome(
        PipelineOutcomeSelector<TState> on,
        Func<TState, bool>? when,
        IPipelineNode to,
        string label
    )
    {
        EnsureRouteMode(on.Source, RouteMode.ResultSpecific);
        if (on.Failed)
        {
            TrackFailureRoute(on.Source, when is null ? null : pipeline => when(pipeline.State));
        }
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(on.Source),
            Bind(to),
            pipeline =>
                pipeline?.LatestResult is { } result
                && result.StepId == on.Source.Id
                && result.CaseId == on.CaseId
                && (when is null || when(pipeline.State)),
            label,
            idempotent: false
        );
        return this;
    }

    private void TrackFailureRoute(IPipelineNode source, Func<PipelineMessage<TState>, bool>? when)
    {
        if (!_failureRoutes.TryGetValue(source, out var routes))
        {
            routes = [];
            _failureRoutes.Add(source, routes);
        }
        routes.Add(when);
    }

    private ExecutorBinding Bind<TResult>(IGeneratedPipelineStep<TState, TResult> step)
    {
        if (_bindings.TryGetValue(step, out var binding))
        {
            return binding;
        }

        var descriptor = step.Descriptor;
        binding = descriptor.Bind();
        _bindings.Add(step, binding);
        _descriptors.Add(step, descriptor);
        return binding;
    }

    private void EnsureInteraction<TRequest, TResponse>(
        PipelineInteraction<TState, TRequest, TResponse> interaction
    )
    {
        if (!_interactions.Add(interaction))
        {
            return;
        }
        _builder.AddEdge(Bind(interaction.Request), Bind(interaction.Port), idempotent: false);
        _builder.AddEdge(Bind(interaction.Port), Bind(interaction.Resume), idempotent: false);
    }

    private ExecutorBinding Bind(IPipelineNode node)
    {
        if (_bindings.TryGetValue(node, out var binding))
        {
            return binding;
        }

        var descriptor = node.Descriptor;
        binding = descriptor.Bind();
        _bindings.Add(node, binding);
        _descriptors.Add(node, descriptor);
        return binding;
    }

    private void EnsureRouteMode(IPipelineNode source, RouteMode mode)
    {
        if (_routeModes.TryGetValue(source, out var existing) && existing != mode)
        {
            throw new InvalidOperationException(
                $"Step '{source.Id}' cannot mix unconditional and outcome-specific outgoing routes."
            );
        }

        _routeModes[source] = mode;
    }

    private enum RouteMode
    {
        Output,
        ResultSpecific,
    }

    private sealed class PipelineStepReferenceComparer : IEqualityComparer<IPipelineNode>
    {
        public static PipelineStepReferenceComparer Instance { get; } = new();

        public bool Equals(IPipelineNode? x, IPipelineNode? y) => ReferenceEquals(x, y);

        public int GetHashCode(IPipelineNode value) => RuntimeHelpers.GetHashCode(value);
    }
}

public static class PipelineResultPayload
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static PipelineResult Create<TCase>(string stepId, string caseId, TCase value) =>
        new(stepId, caseId, JsonSerializer.SerializeToElement(value));
}
