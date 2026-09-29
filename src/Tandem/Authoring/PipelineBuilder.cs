using System.Runtime.CompilerServices;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem;

public sealed class PipelineBuilder<TState>
{
    private readonly WorkflowBuilder _builder;
    private readonly Dictionary<IPipelineNode, ExecutorBinding> _bindings = new(
        PipelineStepReferenceComparer.Instance
    );
    private readonly Dictionary<IPipelineNode, ExecutorBinding> _inputBindings = new(
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
    private readonly Dictionary<IPipelineNode, bool> _persistenceOverrides = new(
        PipelineStepReferenceComparer.Instance
    );
    private readonly HashSet<IPipelineNode> _ownedParallelBranches = new(
        PipelineStepReferenceComparer.Instance
    );
    private readonly List<PipelineParallelInspection> _parallelGroups = [];
    private readonly List<PipelineCollectionInspection> _collections = [];
    private readonly Dictionary<string, string> _physicalSemanticIds = new(StringComparer.Ordinal);
    private readonly Dictionary<
        IPipelineInteractionDefinition,
        bool
    > _interactionPersistenceOverrides = new(ReferenceEqualityComparer.Instance);
    private bool _persistByDefault;
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
        var bound = BindDescriptor(descriptor);
        var result = new PipelineBuilder<TState>(NewWorkflow(bound.Entry, name, description));
        result.Register(start, descriptor, bound);
        return result;
    }

    internal static PipelineBuilder<TState> Create<TRequest, TResponse>(
        PipelineInteraction<TState, TRequest, TResponse> start,
        string name,
        string? description
    )
    {
        var binding = start.Request.Descriptor.Bind();
        var result = new PipelineBuilder<TState>(NewWorkflow(binding, name, description));
        result._bindings.Add(start.Request, binding);
        result._descriptors.Add(start.Request, start.Request.Descriptor);
        result.EnsureInteraction(start);
        return result;
    }

    private static WorkflowBuilder NewWorkflow(
        ExecutorBinding start,
        string name,
        string? description
    )
    {
        var workflowBuilder = new WorkflowBuilder(start).WithName(name);
        return string.IsNullOrWhiteSpace(description)
            ? workflowBuilder
            : workflowBuilder.WithDescription(description);
    }

    private sealed record NodeBinding(
        ExecutorBinding Entry,
        ExecutorBinding Exit,
        StandardOutcomeRouteAwareness<TState>? RouteAwareness,
        ParallelGraphBinding? Parallel
    );

    private static NodeBinding BindDescriptor(PipelineNodeDescriptor descriptor)
    {
        switch (descriptor)
        {
            case PipelineParallelDescriptor<TState> parallel:
            {
                var awareness = new StandardOutcomeRouteAwareness<TState>();
                var graph = parallel.BindGraph(awareness);
                return new(graph.Entry, graph.Exit, awareness, graph);
            }
            case GeneratedOutcomeStepDescriptor<TState> outcome:
            {
                var awareness = new StandardOutcomeRouteAwareness<TState>();
                var binding = outcome.Bind(awareness);
                return new(binding, binding, awareness, null);
            }
            default:
            {
                var binding = descriptor.Bind();
                return new(binding, binding, null, null);
            }
        }
    }

    private void Register(IPipelineNode node, PipelineNodeDescriptor descriptor, NodeBinding bound)
    {
        RegisterCollection(node, descriptor);
        _bindings.Add(node, bound.Exit);
        _descriptors.Add(node, descriptor);
        if (bound.RouteAwareness is { } awareness)
        {
            _failureRouteAwareness.Add(node, awareness);
        }
        if (bound.Parallel is { } graph)
        {
            RegisterPhysicalIds(node.Id, graph.PhysicalIds);
            graph.AddTo(_builder);
            _inputBindings.Add(node, graph.Entry);
            RegisterParallelBranches(node, (PipelineParallelDescriptor<TState>)descriptor);
        }
    }

    public PipelineBuilder<TState> Persist()
    {
        EnsureNotBuilt();
        _persistByDefault = true;
        return this;
    }

    public PipelineBuilder<TState> DoNotPersist()
    {
        EnsureNotBuilt();
        _persistByDefault = false;
        return this;
    }

    public PipelineBuilder<TState> Persist(IPipelineNode<TState> step) =>
        SetPersistence(step, persist: true);

    public PipelineBuilder<TState> DoNotPersist(IPipelineNode<TState> step) =>
        SetPersistence(step, persist: false);

    public PipelineBuilder<TState> Persist<TRequest, TResponse>(
        PipelineInteraction<TState, TRequest, TResponse> interaction
    ) => SetPersistence(interaction, persist: true);

    public PipelineBuilder<TState> DoNotPersist<TRequest, TResponse>(
        PipelineInteraction<TState, TRequest, TResponse> interaction
    ) => SetPersistence(interaction, persist: false);

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

    public PipelineBuilder<TState> Route<TSourceResult>(
        IGeneratedPipelineStep<TState, TSourceResult> on,
        IPipelineNode<TState> to,
        string label
    )
    {
        EnsureRouteMode(on, RouteMode.Output);
        TrackFailureRoute(on, when: null);
        AddRoute(on, to, _ => true, label, unconditional: true);
        return this;
    }

    public PipelineBuilder<TState> Route<TSourceResult, TRequest, TResponse>(
        IGeneratedPipelineStep<TState, TSourceResult> on,
        PipelineInteraction<TState, TRequest, TResponse> to,
        string label
    )
    {
        EnsureRouteMode(on, RouteMode.Output);
        TrackFailureRoute(on, when: null);
        EnsureInteraction(to);
        AddRoute(on, to.Request, _ => true, label, unconditional: true);
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

    public PipelineBuilder<TState> Route<TSourceResult, TRequest, TResponse>(
        Func<TState, bool> when,
        IGeneratedPipelineStep<TState, TSourceResult> from,
        PipelineInteraction<TState, TRequest, TResponse> to,
        string label
    )
    {
        EnsureRouteMode(from, RouteMode.Output);
        TrackFailureRoute(from, pipeline => when(pipeline.State));
        EnsureInteraction(to);
        AddRoute(from, to.Request, pipeline => when(pipeline.State), label);
        return this;
    }

    public PipelineBuilder<TState> Route<TRequest, TResponse>(
        PipelineInteraction<TState, TRequest, TResponse> from,
        IPipelineNode<TState> to,
        string label
    )
    {
        EnsureInteraction(from);
        AddRoute(from.Resume, to, _ => true, label, unconditional: true);
        return this;
    }

    public PipelineBuilder<TState> Route<
        TSourceRequest,
        TSourceResponse,
        TTargetRequest,
        TTargetResponse
    >(
        PipelineInteraction<TState, TSourceRequest, TSourceResponse> from,
        PipelineInteraction<TState, TTargetRequest, TTargetResponse> to,
        string label
    )
    {
        EnsureInteraction(from);
        EnsureInteraction(to);
        AddRoute(from.Resume, to.Request, _ => true, label, unconditional: true);
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

    public PipelineBuilder<TState> Route<
        TSourceRequest,
        TSourceResponse,
        TTargetRequest,
        TTargetResponse
    >(
        Func<TState, bool> when,
        PipelineInteraction<TState, TSourceRequest, TSourceResponse> from,
        PipelineInteraction<TState, TTargetRequest, TTargetResponse> to,
        string label
    )
    {
        EnsureInteraction(from);
        EnsureInteraction(to);
        AddRoute(from.Resume, to.Request, pipeline => when(pipeline.State), label);
        return this;
    }

    public Pipeline<TState> Build(params IPipelineNode<TState>[] outputs)
    {
        if (_built)
        {
            throw new InvalidOperationException("A pipeline builder can build only once.");
        }

        if (outputs.Any(output => output.Descriptor is PipelineParallelDescriptor<TState>))
        {
            throw new InvalidOperationException("A parallel group cannot be a pipeline output.");
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
                            [BindInput(route.Target)]
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
                .ToArray(),
            ResolvePersistentStepIds(),
            _parallelGroups.ToArray(),
            new Dictionary<string, string>(_physicalSemanticIds, StringComparer.Ordinal),
            _collections.ToArray()
        );
        _built = true;
        return pipeline;
    }

    private PipelineBuilder<TState> SetPersistence(IPipelineNode<TState> step, bool persist)
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(step);
        _persistenceOverrides[step] = persist;
        return this;
    }

    private PipelineBuilder<TState> SetPersistence(
        IPipelineInteractionDefinition interaction,
        bool persist
    )
    {
        EnsureNotBuilt();
        ArgumentNullException.ThrowIfNull(interaction);
        _interactionPersistenceOverrides[interaction] = persist;
        return this;
    }

    private IReadOnlySet<string> ResolvePersistentStepIds()
    {
        foreach (var step in _persistenceOverrides.Keys)
        {
            if (!_bindings.ContainsKey(step) && !_ownedParallelBranches.Contains(step))
            {
                throw new InvalidOperationException(
                    $"Persistence policy references unregistered step '{step.Id}'."
                );
            }
        }
        foreach (var interaction in _interactionPersistenceOverrides.Keys)
        {
            if (!_interactions.Contains(interaction))
            {
                throw new InvalidOperationException(
                    $"Persistence policy references unregistered interaction '{interaction.Id}'."
                );
            }
        }

        var interactionIds = _interactions
            .Select(value => value.Id)
            .ToHashSet(StringComparer.Ordinal);
        string SemanticId(string id) =>
            interactionIds.FirstOrDefault(interactionId =>
                id == interactionId
                || id == $"{interactionId}--request"
                || id == $"{interactionId}--resume"
            ) ?? id;
        var policies = _descriptors
            .Keys.Select(step => SemanticId(step.Id))
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(id => id, _ => _persistByDefault, StringComparer.Ordinal);
        foreach (var (step, persist) in _persistenceOverrides)
        {
            policies[SemanticId(step.Id)] = persist;
        }
        foreach (var (interaction, persist) in _interactionPersistenceOverrides)
        {
            policies[interaction.Id] = persist;
        }
        return policies
            .Where(entry => entry.Value)
            .Select(entry => entry.Key)
            .ToHashSet(StringComparer.Ordinal);
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
            unconditionalCase: when is null ? on.CaseId : null
        );
        return this;
    }

    private void AddRoute(
        IPipelineNode source,
        IPipelineNode target,
        Func<PipelineMessage<TState>, bool> predicate,
        string label,
        bool unconditional = false,
        string? unconditionalCase = null
    )
    {
        EnsureNotBuilt();
        Bind(source);
        BindInput(target);
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
        // Routes are evaluated in order, so a second unconditional route for an outcome is dead.
        if (
            unconditionalCase is not null
            && routes.Any(route => route.UnconditionalCase == unconditionalCase)
        )
        {
            throw new InvalidOperationException(
                $"Step '{source.Id}' cannot declare more than one unconditional {unconditionalCase} route."
            );
        }
        routes.Add(
            new PipelineRouteRegistration(
                target,
                predicate,
                label,
                unconditional,
                unconditionalCase
            )
        );
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
        if (_ownedParallelBranches.Contains(node))
        {
            throw new InvalidOperationException(
                $"Parallel branch participant '{node.Id}' cannot also be a parent pipeline node."
            );
        }
        if (_bindings.TryGetValue(node, out var binding))
        {
            return binding;
        }
        if (
            _descriptors.Keys.Any(existing =>
                !ReferenceEquals(existing, node)
                && string.Equals(existing.Id, node.Id, StringComparison.Ordinal)
            ) || _physicalSemanticIds.ContainsKey(node.Id)
        )
        {
            throw new InvalidOperationException(
                $"Pipeline participant ID '{node.Id}' must be globally unique."
            );
        }

        var descriptor = node.Descriptor;
        var bound = BindDescriptor(descriptor);
        Register(node, descriptor, bound);
        return bound.Exit;
    }

    private void RegisterCollection(IPipelineNode node, PipelineNodeDescriptor descriptor)
    {
        if (descriptor is ICollectionDescriptor collection)
        {
            foreach (var agent in collection.Agents)
            {
                if (
                    agent.Id == node.Id
                    || _physicalSemanticIds.ContainsKey(agent.Id)
                    || _descriptors.Keys.Any(existing => existing.Id == agent.Id)
                    || !_ownedParallelBranches.Add(agent)
                )
                {
                    throw new InvalidOperationException(
                        $"Collection agent '{agent.Id}' must have unique ownership."
                    );
                }
                _descriptors.Add(agent, agent.Descriptor);
            }
            _collections.Add(
                new(node.Id, collection.Max, collection.Agents.Select(agent => agent.Id).ToArray())
            );
        }
    }

    private ExecutorBinding BindInput(IPipelineNode node)
    {
        _ = Bind(node);
        return _inputBindings.GetValueOrDefault(node) ?? _bindings[node];
    }

    private void RegisterParallelBranches(
        IPipelineNode group,
        PipelineParallelDescriptor<TState> descriptor
    )
    {
        foreach (var branch in descriptor.Branches)
        {
            if (
                _bindings.Keys.Any(node => ReferenceEquals(node, branch.Participant))
                || !_ownedParallelBranches.Add(branch.Participant)
            )
            {
                throw new InvalidOperationException(
                    $"Parallel branch participant '{branch.Participant.Id}' cannot also be a parent pipeline node."
                );
            }
            if (
                _descriptors.Keys.Any(node =>
                    string.Equals(node.Id, branch.Participant.Id, StringComparison.Ordinal)
                ) || _physicalSemanticIds.ContainsKey(branch.Participant.Id)
            )
            {
                throw new InvalidOperationException(
                    $"Pipeline participant ID '{branch.Participant.Id}' must be globally unique."
                );
            }
            _descriptors.Add(branch.Participant, branch.Participant.Descriptor);
        }
        _parallelGroups.Add(
            new PipelineParallelInspection(
                group.Id,
                descriptor
                    .Branches.Select(
                        (branch, index) =>
                            new PipelineParallelBranchInspection(
                                branch.Id,
                                index,
                                branch.Participant.Id
                            )
                    )
                    .ToArray()
            )
        );
    }

    private void RegisterPhysicalIds(string semanticId, IReadOnlySet<string> physicalIds)
    {
        foreach (var physicalId in physicalIds)
        {
            if (
                physicalId != semanticId
                && _descriptors.Keys.Any(node =>
                    string.Equals(node.Id, physicalId, StringComparison.Ordinal)
                )
            )
            {
                throw new InvalidOperationException(
                    $"Physical pipeline node ID '{physicalId}' conflicts with an authored participant ID."
                );
            }
            if (!_physicalSemanticIds.TryAdd(physicalId, semanticId))
            {
                throw new InvalidOperationException(
                    $"Physical pipeline node ID '{physicalId}' is already registered."
                );
            }
        }
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
        bool Unconditional,
        string? UnconditionalCase = null
    );

    private sealed class PipelineStepReferenceComparer : IEqualityComparer<IPipelineNode>
    {
        public static PipelineStepReferenceComparer Instance { get; } = new();

        public bool Equals(IPipelineNode? x, IPipelineNode? y) => ReferenceEquals(x, y);

        public int GetHashCode(IPipelineNode value) => RuntimeHelpers.GetHashCode(value);
    }
}
