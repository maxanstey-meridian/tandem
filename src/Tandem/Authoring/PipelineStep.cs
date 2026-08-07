using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Tandem.Domain;
using Tandem.Infrastructure.Projection;

namespace Tandem;

public interface IPipelineNode
{
    public string Id { get; }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public PipelineNodeDescriptor Descriptor { get; }
}

public interface IRawPipelineNode : IPipelineNode;

public interface IPipelineStep<TResult> : IPipelineNode;

public abstract class PipelineNodeDescriptor
{
    internal abstract ExecutorBinding Bind();
}

public interface IPipelineExecutionContext
{
    public ValueTask QueueStateUpdateAsync(
        string key,
        string value,
        string scopeName,
        CancellationToken cancellationToken
    );

    public ValueTask<HashSet<string>> ReadStateKeysAsync(
        string scopeName,
        CancellationToken cancellationToken
    );

    public ValueTask<T?> ReadStateAsync<T>(
        string key,
        string scopeName,
        CancellationToken cancellationToken
    );
}

public static class PipelineNodes
{
    public static PipelineNodeDescriptor Stage<TInput, TOutput>(
        string id,
        Func<TInput, IPipelineExecutionContext, CancellationToken, ValueTask<TOutput>> execute,
        IBlockExecutionObserver? observer = null
    ) => new DelegatePipelineNodeDescriptor<TInput, TOutput>(id, execute, observer);

    public static PipelineNodeDescriptor RequestPort<TRequest, TResponse>(string id) =>
        new RequestPortPipelineNodeDescriptor<TRequest, TResponse>(id);

    public static IRawPipelineNode Failed<TState>(string id) => new TerminalFailedNode<TState>(id);

    public static PipelineRequest<TState, TRequest, TResponse> Request<TState, TRequest, TResponse>(
        string requestStepId,
        string portId,
        string resumeStepId,
        Func<TState, TRequest> createRequest,
        Func<TState, TResponse, TState> applyResponse,
        IBlockExecutionObserver? observer = null
    ) => new(requestStepId, portId, resumeStepId, createRequest, applyResponse, observer);
}

internal sealed class TerminalFailedNode<TState>(string id) : IRawPipelineNode
{
    public string Id => id;

    public PipelineNodeDescriptor Descriptor { get; } =
        PipelineNodes.Stage<PipelineMessage<TState>, PipelineMessage<TState>>(
            id,
            (message, _, _) =>
                ValueTask.FromResult(message with { Disposition = PipelineRunDisposition.Failed })
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
public sealed class GeneratedCustomStepDescriptor<TState, TResult>(
    string id,
    Func<TState, CancellationToken, ValueTask<TResult>> execute,
    Func<PipelineMessage<TState>, TResult, PipelineMessage<TState>> adapt
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind() =>
        new GeneratedCustomStepExecutor<TState, TResult>(id, execute, adapt).Bind();
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

public readonly struct ResultCase<TState, TResult, TCase>
    where TCase : TResult
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ResultCase(IGeneratedPipelineStep<TState, TResult> source, string caseId)
    {
        if (!string.Equals(caseId, typeof(TCase).Name, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Result case id '{caseId}' does not match case type '{typeof(TCase).Name}'.",
                nameof(caseId)
            );
        }
        Source = source;
        CaseId = caseId;
    }

    internal IGeneratedPipelineStep<TState, TResult> Source { get; }
    internal string CaseId { get; }
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

internal sealed class GeneratedCustomStepExecutor<TState, TResult>(
    string id,
    Func<TState, CancellationToken, ValueTask<TResult>> execute,
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
        var result = await execute(pipeline.State, cancellationToken);
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
        var routes = Workflow
            .ReflectEdges()
            .SelectMany(entry =>
                entry.Value.Select(edge => new PipelineRouteInspection(
                    edge.Connection.SourceIds.Single(),
                    edge.Connection.SinkIds.Single(),
                    edge is DirectEdgeInfo direct && direct.HasCondition
                ))
            )
            .ToArray();
        var ports = Workflow
            .ReflectPorts()
            .Values.Select(port => new PipelinePortInspection(
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
            Workflow.ReflectExecutors().Keys.Order(StringComparer.Ordinal).ToArray(),
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

    public PipelineBuilder<TState> Route<TSourceResult, TCase, TTargetResult>(
        ResultCase<TState, TSourceResult, TCase> on,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
        where TCase : TSourceResult
    {
        EnsureRouteMode(on.Source, RouteMode.ResultSpecific);
        TrackFailedResultRoute(on, when: null);
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(on.Source),
            Bind(to),
            pipeline =>
                pipeline?.LatestResult is { } result
                && result.StepId == on.Source.Id
                && result.CaseId == on.CaseId,
            label,
            idempotent: false
        );
        return this;
    }

    public PipelineBuilder<TState> Route<TSourceResult, TCase>(
        ResultCase<TState, TSourceResult, TCase> on,
        IRawPipelineNode to,
        string label
    )
        where TCase : TSourceResult
    {
        EnsureRouteMode(on.Source, RouteMode.ResultSpecific);
        TrackFailedResultRoute(on, when: null);
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(on.Source),
            Bind(to),
            pipeline =>
                pipeline?.LatestResult is { } result
                && result.StepId == on.Source.Id
                && result.CaseId == on.CaseId,
            label,
            idempotent: false
        );
        return this;
    }

    public PipelineBuilder<TState> Route<TSourceResult, TCase, TTargetResult>(
        ResultCase<TState, TSourceResult, TCase> on,
        Func<TState, bool> when,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
        where TCase : TSourceResult
    {
        EnsureRouteMode(on.Source, RouteMode.ResultSpecific);
        TrackFailedResultRoute(on, pipeline => when(pipeline.State));
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(on.Source),
            Bind(to),
            pipeline =>
                pipeline?.LatestResult is { } result
                && result.StepId == on.Source.Id
                && result.CaseId == on.CaseId
                && when(pipeline.State),
            label,
            idempotent: false
        );
        return this;
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

    public PipelineBuilder<TState> Route(IRawPipelineNode from, IRawPipelineNode to, string label)
    {
        EnsureRouteMode(from, RouteMode.Output);
        TrackFailureRoute(from, when: null);
        _builder.AddEdge(Bind(from), Bind(to), label, idempotent: false);
        return this;
    }

    public PipelineBuilder<TState> Route<TTargetResult>(
        Func<TState, bool> when,
        IRawPipelineNode from,
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

    private void TrackFailedResultRoute<TSourceResult, TCase>(
        ResultCase<TState, TSourceResult, TCase> route,
        Func<PipelineMessage<TState>, bool>? when
    )
        where TCase : TSourceResult
    {
        if (route.CaseId == nameof(Outcome<TState>.Failed))
        {
            TrackFailureRoute(route.Source, when);
        }
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
                $"Step '{source.Id}' cannot mix unconditional and result-specific outgoing routes."
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
