using Microsoft.Agents.AI.Workflows;

namespace Tandem;

public sealed class Pipeline<TState>
{
    private readonly IReadOnlyList<string> _outputStepIds;
    private readonly IReadOnlyList<PipelineRouteInspection> _routes;
    private readonly IReadOnlyList<PipelineInteractionInspection> _interactions;
    private readonly IReadOnlySet<string> _persistentStepIds;
    private readonly IReadOnlyList<PipelineParallelInspection> _parallelGroups;
    private readonly IReadOnlyDictionary<string, string> _physicalSemanticIds;
    private readonly IReadOnlyList<PipelineCollectionInspection> _collections;

    internal Pipeline(
        Workflow workflow,
        IReadOnlyList<string> outputStepIds,
        IReadOnlyList<PipelineRouteInspection> routes,
        IReadOnlyList<PipelineInteractionInspection> interactions,
        IReadOnlySet<string>? persistentStepIds = null,
        IReadOnlyList<PipelineParallelInspection>? parallelGroups = null,
        IReadOnlyDictionary<string, string>? physicalSemanticIds = null,
        IReadOnlyList<PipelineCollectionInspection>? collections = null
    )
    {
        Workflow = workflow;
        _collections = collections ?? [];
        _outputStepIds = outputStepIds;
        _routes = routes;
        _interactions = interactions;
        _persistentStepIds = persistentStepIds ?? new HashSet<string>(StringComparer.Ordinal);
        _parallelGroups = parallelGroups ?? [];
        _physicalSemanticIds =
            physicalSemanticIds ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    internal Workflow Workflow { get; }
    internal IReadOnlySet<string> PersistentStepIds => _persistentStepIds;
    internal bool RequiresPersistence => _persistentStepIds.Count > 0;

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
            if (_physicalSemanticIds.TryGetValue(id, out var semanticId))
            {
                return semanticId;
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
        var stepIds = physicalStepIds
            .Select(SemanticId)
            .Concat(_collections.SelectMany(collection => collection.AgentIds))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var startStepId = SemanticId(Workflow.StartExecutorId);
        return new PipelineInspection(
            Workflow.Name ?? throw new InvalidOperationException("Pipeline name is unavailable."),
            Workflow.Description,
            startStepId,
            stepIds,
            _interactions,
            routes
                .OrderBy(route => route.SourceId, StringComparer.Ordinal)
                .ThenBy(route => route.TargetId, StringComparer.Ordinal)
                .ThenBy(route => route.Conditional)
                .ToArray(),
            _outputStepIds,
            stepIds.Where(_persistentStepIds.Contains).ToArray()
        )
        {
            ParallelGroups = _parallelGroups,
            Collections = _collections,
        };
    }
}

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

    public static PipelineBuilder<TState> Start<TState, TRequest, TResponse>(
        PipelineInteraction<TState, TRequest, TResponse> at,
        string name,
        string? description = null
    ) => PipelineBuilder<TState>.Create(at, name, description);
}
