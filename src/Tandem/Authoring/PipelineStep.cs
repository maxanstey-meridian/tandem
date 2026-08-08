using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

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

[EditorBrowsable(EditorBrowsableState.Never)]
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
        Func<string, string, string> summarize
    ) => new StateTransitionFailedNode<TState>(id, transition, outcomeKind, summarize);

    public static IPipelineNode<TState> Complete<TState>(string id) =>
        new TerminalCompleteNode<TState>(id);

    public static IPipelineNode<TState> Complete<TState>(
        string id,
        Func<TState, TState> transition,
        string outcomeKind,
        string summary
    ) => new StateTransitionCompleteNode<TState>(id, transition, outcomeKind, summary);

    public static PipelineInteraction<TState, TRequest, TResponse> WaitFor<
        TState,
        TRequest,
        TResponse
    >(
        string id,
        Func<TState, TRequest> createRequest,
        Func<TState, TResponse, TState> applyResponse
    ) => new(id, createRequest, applyResponse);
}

internal sealed class TerminalCompleteNode<TState>(string id)
    : IPipelineNode<TState>,
        IRawPipelineNode
{
    public string Id => id;

    public PipelineNodeDescriptor Descriptor { get; } =
        CorePipelineNodes.Stage<PipelineMessage<TState>, PipelineMessage<TState>>(
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
                            new { }
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
        CorePipelineNodes.Stage<PipelineMessage<TState>, PipelineMessage<TState>>(
            id,
            (message, _, _) =>
                ValueTask.FromResult(message with { Disposition = PipelineRunDisposition.Failed })
        );
}

internal sealed class StateTransitionCompleteNode<TState>(
    string id,
    Func<TState, TState> transition,
    string outcomeKind,
    string summary
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
            }
        );
}

internal sealed class StateTransitionFailedNode<TState>(
    string id,
    Func<TState, TState> transition,
    string outcomeKind,
    Func<string, string, string> summarize
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
            }
        );
}

internal sealed class DelegatePipelineNodeDescriptor<TInput, TOutput>(
    string id,
    Func<TInput, IPipelineExecutionContext, CancellationToken, ValueTask<TOutput>> execute,
    string? observationId = null,
    PipelineObservationMode observationMode = PipelineObservationMode.Full
) : PipelineNodeDescriptor
{
    internal override ExecutorBinding Bind()
    {
        var executor = new DelegatePipelineNodeExecutor<TInput, TOutput>(
            id,
            observationId ?? id,
            observationMode,
            execute
        );
        return executor.BindExecutor();
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
    string observationId,
    PipelineObservationMode observationMode,
    Func<TInput, IPipelineExecutionContext, CancellationToken, ValueTask<TOutput>> execute
) : Executor<TInput, TOutput>(id, options: null, declareCrossRunShareable: true)
{
    public override async ValueTask<TOutput> HandleAsync(
        TInput input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    ) =>
        await PipelineObservationPublisher.ExecuteAsync(
            observationId,
            observationMode,
            input,
            () => execute(input, new PipelineExecutionContext(), cancellationToken),
            cancellationToken
        );
}

internal sealed class PipelineExecutionContext : IPipelineExecutionContext;

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
    internal override ExecutorBinding Bind() => Bind(new StandardOutcomeRouteAwareness<TState>());

    internal ExecutorBinding Bind(StandardOutcomeRouteAwareness<TState> routeAwareness) =>
        new GeneratedOutcomeStepExecutor<TState>(id, execute, routeAwareness).Bind();
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
)
    : Executor<PipelineMessage<TState>, PipelineMessage<TState>>(
        id,
        options: null,
        declareCrossRunShareable: true
    )
{
    internal ExecutorBinding Bind() => this.BindExecutor();

    public override async ValueTask<PipelineMessage<TState>> HandleAsync(
        PipelineMessage<TState> pipeline,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        using var envelope = PipelineExecutionEnvelope.Begin(pipeline);
        return await PipelineObservationPublisher.ExecuteAsync(
            Id,
            PipelineObservationMode.Full,
            pipeline,
            async () =>
            {
                var result = await execute(pipeline, cancellationToken);
                return adapt(envelope.Message, result);
            },
            cancellationToken
        );
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
        : base(id, options: null, declareCrossRunShareable: true)
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
        return await PipelineObservationPublisher.ExecuteAsync(
            _id,
            PipelineObservationMode.Full,
            pipeline,
            async () =>
            {
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
            },
            cancellationToken
        );
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
        : base(id, options: null, declareCrossRunShareable: true)
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
        return await PipelineObservationPublisher.ExecuteAsync(
            _id,
            PipelineObservationMode.Full,
            pipeline,
            async () =>
            {
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
                        new { }
                    ),
                };
            },
            cancellationToken
        );
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
        : base(id, options: null, declareCrossRunShareable: true)
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
        return await PipelineObservationPublisher.ExecuteAsync(
            _id,
            PipelineObservationMode.Full,
            pipeline,
            async () =>
            {
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
                            new { }
                        ),
                    },
                    Outcome<TState>.Failed failed => AdaptFailed(envelope.Message, failed),
                    _ => throw new InvalidOperationException("Unknown standard outcome."),
                };
            },
            cancellationToken
        );
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
                failed.Failure
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

public sealed class Pipeline<TState>
{
    private readonly IReadOnlyList<string> _outputStepIds;
    private readonly IReadOnlyList<PipelineRouteInspection> _routes;
    private readonly IReadOnlyList<PipelineInteractionInspection> _interactions;

    internal Pipeline(
        Workflow workflow,
        IReadOnlyList<string> outputStepIds,
        IReadOnlyList<PipelineRouteInspection> routes,
        IReadOnlyList<PipelineInteractionInspection> interactions
    )
    {
        Workflow = workflow;
        _outputStepIds = outputStepIds;
        _routes = routes;
        _interactions = interactions;
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

        var routes = _routes
            .Select(route =>
                route with
                {
                    SourceId = SemanticId(route.SourceId),
                    TargetId = SemanticId(route.TargetId),
                }
            )
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
        var stepIds = physicalStepIds
            .Select(SemanticId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var semanticRoutes = routes
            .OrderBy(route => route.SourceId, StringComparer.Ordinal)
            .ThenBy(route => route.TargetId, StringComparer.Ordinal)
            .ThenBy(route => route.Conditional)
            .ToArray();
        return new PipelineInspection(
            Workflow.Name ?? throw new InvalidOperationException("Pipeline name is unavailable."),
            Workflow.Description,
            Workflow.StartExecutorId,
            stepIds,
            ports,
            _interactions,
            semanticRoutes,
            _outputStepIds,
            RenderMermaid(stepIds, semanticRoutes, Workflow.StartExecutorId, _outputStepIds),
            RenderDot(stepIds, semanticRoutes, Workflow.StartExecutorId, _outputStepIds)
        );
    }

    private static string RenderMermaid(
        IReadOnlyList<string> stepIds,
        IReadOnlyList<PipelineRouteInspection> routes,
        string startStepId,
        IReadOnlyList<string> outputStepIds
    )
    {
        var aliases = stepIds
            .Select((id, index) => (id, alias: $"n{index}"))
            .ToDictionary(item => item.id, item => item.alias, StringComparer.Ordinal);
        var lines = new List<string> { "flowchart TD" };
        lines.AddRange(
            stepIds.Select(id =>
            {
                var label = $"\"{Escape(id)}\"";
                return id == startStepId ? $"    {aliases[id]}(({label}))"
                    : outputStepIds.Contains(id, StringComparer.Ordinal)
                        ? $"    {aliases[id]}{{{{{label}}}}}"
                    : $"    {aliases[id]}[{label}]";
            })
        );
        lines.AddRange(
            routes.Select(route =>
            {
                var label = string.IsNullOrWhiteSpace(route.Label)
                    ? ""
                    : $"|\"{Escape(route.Label)}\"|";
                var arrow = route.Conditional ? "-.->" : "-->";
                return $"    {aliases[route.SourceId]} {arrow}{label} {aliases[route.TargetId]}";
            })
        );
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderDot(
        IReadOnlyList<string> stepIds,
        IReadOnlyList<PipelineRouteInspection> routes,
        string startStepId,
        IReadOnlyList<string> outputStepIds
    )
    {
        var aliases = stepIds
            .Select((id, index) => (id, alias: $"n{index}"))
            .ToDictionary(item => item.id, item => item.alias, StringComparer.Ordinal);
        var lines = new List<string> { "digraph pipeline {" };
        lines.AddRange(
            stepIds.Select(id =>
            {
                var shape =
                    id == startStepId ? ", shape=doublecircle"
                    : outputStepIds.Contains(id, StringComparer.Ordinal) ? ", shape=box"
                    : "";
                return $"  {aliases[id]} [label=\"{Escape(id)}\"{shape}];";
            })
        );
        lines.AddRange(
            routes.Select(route =>
            {
                var attributes = new List<string>();
                if (!string.IsNullOrWhiteSpace(route.Label))
                {
                    attributes.Add($"label=\"{Escape(route.Label)}\"");
                }
                if (route.Conditional)
                {
                    attributes.Add("style=dashed");
                }
                var suffix = attributes.Count == 0 ? "" : $" [{string.Join(", ", attributes)}]";
                return $"  {aliases[route.SourceId]} -> {aliases[route.TargetId]}{suffix};";
            })
        );
        lines.Add("}");
        return string.Join(Environment.NewLine, lines);
    }

    private static string Escape(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}

public sealed record PipelineInspection(
    string Name,
    string? Description,
    string StartStepId,
    IReadOnlyList<string> StepIds,
    IReadOnlyList<PipelinePortInspection> Ports,
    IReadOnlyList<PipelineInteractionInspection> Interactions,
    IReadOnlyList<PipelineRouteInspection> Routes,
    IReadOnlyList<string> OutputStepIds,
    string Mermaid,
    string Dot
);

public sealed record PipelineRouteInspection(
    string SourceId,
    string TargetId,
    bool Conditional,
    string? Label = null
);

public sealed record PipelinePortInspection(string Id, string InputType, string OutputType);

public sealed record PipelineInteractionInspection(
    string Id,
    string RequestType,
    string ResponseType
);

internal static class PipelineMafBridge
{
    public static Workflow GetWorkflow<TState>(Pipeline<TState> pipeline) => pipeline.Workflow;
}

public static class Pipeline
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
    private readonly HashSet<IPipelineInteractionDefinition> _interactions = [];
    private readonly Dictionary<
        IPipelineNode,
        List<Func<PipelineMessage<TState>, bool>?>
    > _failureRoutes = new(PipelineStepReferenceComparer.Instance);
    private readonly Dictionary<
        IPipelineNode,
        StandardOutcomeRouteAwareness<TState>
    > _failureRouteAwareness = new(PipelineStepReferenceComparer.Instance);
    private readonly Dictionary<IPipelineNode, List<PipelineRouteRegistration>> _routes = new(
        PipelineStepReferenceComparer.Instance
    );
    private bool _built;

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
        var awareness =
            descriptor is GeneratedOutcomeStepDescriptor<TState>
                ? new StandardOutcomeRouteAwareness<TState>()
                : null;
        var binding = awareness is null
            ? descriptor.Bind()
            : ((GeneratedOutcomeStepDescriptor<TState>)descriptor).Bind(awareness);
        var workflowBuilder = new WorkflowBuilder(binding).WithName(name);
        if (!string.IsNullOrWhiteSpace(description))
        {
            workflowBuilder = workflowBuilder.WithDescription(description);
        }
        var result = new PipelineBuilder<TState>(workflowBuilder);
        result._bindings.Add(start, binding);
        result._descriptors.Add(start, descriptor);
        if (awareness is not null)
        {
            result._failureRouteAwareness.Add(start, awareness);
        }
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
        AddRoute(on, to, _ => true, label, unconditional: true);
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
        AddRoute(from, to, pipeline => when(pipeline.State), label);
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
        AddRoute(from, to, pipeline => when(pipeline.State), label);
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
        AddRoute(from.Resume, to, pipeline => when(pipeline.State), label);
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
        AddRoute(from.Resume, to, pipeline => when(pipeline.State), label);
        return this;
    }

    public Pipeline<TState> Build(params IPipelineNode<TState>[] outputs)
    {
        if (_built)
        {
            throw new InvalidOperationException("A pipeline builder can build only once.");
        }

        var outputBindings = outputs.Select(Bind).ToArray();

        foreach (var node in _bindings.Keys)
        {
            if (_failureRouteAwareness.TryGetValue(node, out var awareness))
            {
                var routes = _failureRoutes.GetValueOrDefault(node) ?? [];
                awareness.Matches = message => routes.Any(route => route is null || route(message));
            }
        }

        foreach (var (source, routes) in _routes)
        {
            _builder.AddSwitch(
                Bind(source),
                switchBuilder =>
                {
                    foreach (var route in routes)
                    {
                        switchBuilder.AddCase<PipelineMessage<TState>>(
                            message => message is not null && route.Predicate(message),
                            [Bind(route.Target)]
                        );
                    }
                }
            );
        }

        _builder.WithOutputFrom(outputBindings);
        var pipeline = new Pipeline<TState>(
            _builder.Build(),
            outputs.Select(output => output.Id).ToArray(),
            _routes
                .SelectMany(entry =>
                    entry.Value.Select(route => new PipelineRouteInspection(
                        entry.Key.Id,
                        route.Target.Id,
                        !route.Unconditional,
                        route.Label
                    ))
                )
                .ToArray(),
            _interactions
                .Select(interaction => new PipelineInteractionInspection(
                    interaction.Id,
                    interaction.RequestType.FullName ?? interaction.RequestType.Name,
                    interaction.ResponseType.FullName ?? interaction.ResponseType.Name
                ))
                .OrderBy(interaction => interaction.Id, StringComparer.Ordinal)
                .ToArray()
        );
        _built = true;
        return pipeline;
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
        AddRoute(
            on.Source,
            to,
            pipeline =>
                pipeline?.LatestResult is { } result
                && result.StepId == on.Source.Id
                && result.CaseId == on.CaseId
                && (when is null || when(pipeline.State)),
            label,
            unconditional: false
        );
        return this;
    }

    private void AddRoute(
        IPipelineNode source,
        IPipelineNode target,
        Func<PipelineMessage<TState>, bool> predicate,
        string label,
        bool unconditional = false
    )
    {
        EnsureNotBuilt();
        Bind(source);
        Bind(target);
        if (!_routes.TryGetValue(source, out var routes))
        {
            routes = [];
            _routes.Add(source, routes);
        }
        if (unconditional && routes.Any(route => route.Unconditional))
        {
            throw new InvalidOperationException(
                $"Step '{source.Id}' cannot declare more than one unconditional route."
            );
        }
        routes.Add(new PipelineRouteRegistration(target, predicate, label, unconditional));
    }

    private void TrackFailureRoute(IPipelineNode source, Func<PipelineMessage<TState>, bool>? when)
    {
        EnsureNotBuilt();
        if (!_failureRoutes.TryGetValue(source, out var routes))
        {
            routes = [];
            _failureRoutes.Add(source, routes);
        }
        routes.Add(when);
    }

    private void EnsureInteraction<TRequest, TResponse>(
        PipelineInteraction<TState, TRequest, TResponse> interaction
    )
    {
        EnsureNotBuilt();
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
        if (descriptor is GeneratedOutcomeStepDescriptor<TState> outcomeDescriptor)
        {
            var awareness = new StandardOutcomeRouteAwareness<TState>();
            binding = outcomeDescriptor.Bind(awareness);
            _failureRouteAwareness.Add(node, awareness);
        }
        else
        {
            binding = descriptor.Bind();
        }
        _bindings.Add(node, binding);
        _descriptors.Add(node, descriptor);
        return binding;
    }

    private void EnsureRouteMode(IPipelineNode source, RouteMode mode)
    {
        EnsureNotBuilt();
        if (_routeModes.TryGetValue(source, out var existing) && existing != mode)
        {
            throw new InvalidOperationException(
                $"Step '{source.Id}' cannot mix unconditional and outcome-specific outgoing routes."
            );
        }

        _routeModes[source] = mode;
    }

    private void EnsureNotBuilt()
    {
        if (_built)
        {
            throw new InvalidOperationException("A built pipeline cannot be modified.");
        }
    }

    private enum RouteMode
    {
        Output,
        ResultSpecific,
    }

    private sealed record PipelineRouteRegistration(
        IPipelineNode Target,
        Func<PipelineMessage<TState>, bool> Predicate,
        string Label,
        bool Unconditional
    );

    private sealed class PipelineStepReferenceComparer : IEqualityComparer<IPipelineNode>
    {
        public static PipelineStepReferenceComparer Instance { get; } = new();

        public bool Equals(IPipelineNode? x, IPipelineNode? y) => ReferenceEquals(x, y);

        public int GetHashCode(IPipelineNode value) => RuntimeHelpers.GetHashCode(value);
    }
}

internal static class PipelineResultPayload
{
    public static PipelineResult Create<TCase>(string stepId, string caseId, TCase value) =>
        new(stepId, caseId, JsonSerializer.SerializeToElement(value));
}
