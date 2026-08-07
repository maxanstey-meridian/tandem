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
public interface IGeneratedPipelineStep<TState, TResult> : IPipelineStep<TResult>
{
    public ValueTask<TResult> ExecuteAsync(
        PipelineMessage<TState> pipeline,
        CancellationToken cancellationToken
    );

    public PipelineMessage<TState> AdaptResult(PipelineMessage<TState> pipeline, TResult result);
}

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

public readonly struct ResultCase<TState, TResult, TCase>
    where TCase : TResult
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ResultCase(IGeneratedPipelineStep<TState, TResult> source, string caseId)
    {
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
        var result = await execute(pipeline, cancellationToken);
        return adapt(pipeline, result);
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
        var binding = start.Descriptor.Bind();
        var workflowBuilder = new WorkflowBuilder(binding).WithName(name);
        if (!string.IsNullOrWhiteSpace(description))
        {
            workflowBuilder = workflowBuilder.WithDescription(description);
        }
        var result = new PipelineBuilder<TState>(workflowBuilder);
        result._bindings.Add(start, binding);
        return result;
    }

    public PipelineBuilder<TState> Route<TSourceResult, TCase, TTargetResult>(
        ResultCase<TState, TSourceResult, TCase> on,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
        where TCase : TSourceResult
    {
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
        IPipelineNode to,
        string label
    )
        where TCase : TSourceResult
    {
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
        Func<PipelineMessage<TState>, bool> when,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
        where TCase : TSourceResult
    {
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(on.Source),
            Bind(to),
            pipeline =>
                pipeline?.LatestResult is { } result
                && result.StepId == on.Source.Id
                && result.CaseId == on.CaseId
                && when(pipeline),
            label,
            idempotent: false
        );
        return this;
    }

    public PipelineBuilder<TState> Route<TSourceResult, TTargetResult>(
        IGeneratedPipelineStep<TState, TSourceResult> from,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
    {
        _builder.AddEdge(Bind(from), Bind(to), label, idempotent: false);
        return this;
    }

    public PipelineBuilder<TState> Route<TSourceResult, TTargetResult>(
        Func<PipelineMessage<TState>, bool> when,
        IGeneratedPipelineStep<TState, TSourceResult> from,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
    {
        _builder.AddEdge<PipelineMessage<TState>>(
            Bind(from),
            Bind(to),
            pipeline => pipeline is not null && when(pipeline),
            label,
            idempotent: false
        );
        return this;
    }

    public PipelineBuilder<TState> Route(IPipelineNode from, IPipelineNode to, string label)
    {
        _builder.AddEdge(Bind(from), Bind(to), label, idempotent: false);
        return this;
    }

    public PipelineBuilder<TState> Route<TTargetResult>(
        Func<PipelineMessage<TState>, bool> when,
        IPipelineNode from,
        IGeneratedPipelineStep<TState, TTargetResult> to,
        string label
    )
    {
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
        _builder.WithOutputFrom(outputs.Select(Bind).ToArray());
        return new Pipeline(_builder.Build(), outputs.Select(output => output.Id).ToArray());
    }

    private ExecutorBinding Bind<TResult>(IGeneratedPipelineStep<TState, TResult> step)
    {
        if (_bindings.TryGetValue(step, out var binding))
        {
            return binding;
        }

        binding = step.Descriptor.Bind();
        _bindings.Add(step, binding);
        return binding;
    }

    private ExecutorBinding Bind(IPipelineNode node)
    {
        if (_bindings.TryGetValue(node, out var binding))
        {
            return binding;
        }

        binding = node.Descriptor.Bind();
        _bindings.Add(node, binding);
        return binding;
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
