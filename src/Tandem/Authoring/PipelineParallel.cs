using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem;

public static class PipelineBranch
{
    public static PipelineBranch<TState> Create<TState, TResult>(
        string id,
        IGeneratedPipelineStep<TState, TResult> participant
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(participant);
        return new PipelineBranch<TState>(id, participant);
    }
}

public sealed class PipelineBranch<TState>
{
    internal PipelineBranch(string id, IPipelineNode<TState> participant)
    {
        Id = id;
        Participant = participant;
    }

    public string Id { get; }
    public IPipelineNode<TState> Participant { get; }
}

public sealed class PipelineParallelMerge<TState>
{
    private readonly IReadOnlyDictionary<string, TState> _states;

    internal PipelineParallelMerge(
        TState baseline,
        IReadOnlyList<string> branchIds,
        IReadOnlyDictionary<string, TState> states
    )
    {
        Baseline = baseline;
        BranchIds = branchIds;
        _states = states;
    }

    public TState Baseline { get; }
    public IReadOnlyList<string> BranchIds { get; }

    public TState State(string branchId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchId);
        return _states.TryGetValue(branchId, out var state)
            ? state
            : throw new ArgumentException(
                $"Parallel branch '{branchId}' is not part of this merge.",
                nameof(branchId)
            );
    }
}

public sealed class PipelineParallel<TState> : IStandardOutcomePipelineStep<TState>
{
    internal PipelineParallel(
        string id,
        Func<TState, TState> clone,
        IReadOnlyList<PipelineBranch<TState>> branches,
        Func<PipelineParallelMerge<TState>, TState> merge
    )
    {
        Id = id;
        Descriptor = new PipelineParallelDescriptor<TState>(id, clone, branches, merge);
    }

    public string Id { get; }
    public PipelineNodeDescriptor Descriptor { get; }
    public PipelineOutcomeSelector<TState> Success => new(this, failed: false);
    public PipelineOutcomeSelector<TState> Failed => new(this, failed: true);
}

internal sealed class PipelineParallelDescriptor<TState> : PipelineNodeDescriptor
{
    private readonly string _id;
    private readonly Func<TState, TState> _clone;
    private readonly IReadOnlyList<PipelineBranch<TState>> _branches;
    private readonly Func<PipelineParallelMerge<TState>, TState> _merge;

    public PipelineParallelDescriptor(
        string id,
        Func<TState, TState> clone,
        IReadOnlyList<PipelineBranch<TState>> branches,
        Func<PipelineParallelMerge<TState>, TState> merge
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(clone);
        ArgumentNullException.ThrowIfNull(branches);
        ArgumentNullException.ThrowIfNull(merge);
        if (branches.Count < 2)
        {
            throw new ArgumentException(
                "A parallel group requires at least two branches.",
                nameof(branches)
            );
        }
        if (branches.Any(branch => string.IsNullOrWhiteSpace(branch.Id)))
        {
            throw new ArgumentException("Parallel branch IDs cannot be blank.", nameof(branches));
        }
        if (
            branches.Select(branch => branch.Id).Distinct(StringComparer.Ordinal).Count()
            != branches.Count
        )
        {
            throw new ArgumentException("Parallel branch IDs must be unique.", nameof(branches));
        }
        if (
            branches
                .Select(branch => branch.Participant)
                .Distinct(ReferenceEqualityComparer.Instance)
                .Count() != branches.Count
        )
        {
            throw new ArgumentException(
                "A participant can belong to only one parallel branch.",
                nameof(branches)
            );
        }

        _id = id;
        _clone = clone;
        _branches = branches.ToArray();
        _merge = merge;
    }

    internal IReadOnlyList<PipelineBranch<TState>> Branches => _branches;

    internal override ExecutorBinding Bind() =>
        throw new InvalidOperationException(
            "Parallel groups must be bound through the pipeline builder."
        );

    internal ParallelGraphBinding BindGraph(StandardOutcomeRouteAwareness<TState> routeAwareness)
    {
        var fork = new ParallelForkExecutor<TState>(
            _id,
            _clone,
            _branches.Select(branch => branch.Id).ToArray()
        ).BindExecutor();
        var adapters = _branches
            .Select(
                (branch, index) =>
                    new ParallelBranchAdapterExecutor<TState>(
                        PhysicalId("branch", index, branch.Participant.Id),
                        index
                    ).BindExecutor()
            )
            .ToArray();
        var participants = _branches.Select(BindParticipant).ToArray();
        var exits = _branches
            .Select(
                (branch, index) =>
                    new ParallelBranchExitExecutor<TState>(
                        PhysicalId("exit", index, branch.Participant.Id),
                        _id,
                        branch.Id,
                        index
                    ).BindExecutor()
            )
            .ToArray();
        var join = new ParallelJoinExecutor<TState>(
            _id,
            _branches.Select(branch => branch.Id).ToArray(),
            _merge,
            routeAwareness
        ).BindExecutor();

        return new ParallelGraphBinding(
            fork,
            join,
            new[] { fork.Id, join.Id }
                .Concat(adapters.Select(binding => binding.Id))
                .Concat(exits.Select(binding => binding.Id))
                .ToHashSet(StringComparer.Ordinal),
            builder =>
            {
                builder.AddFanOutEdge(fork, adapters);
                for (var index = 0; index < adapters.Length; index++)
                {
                    builder.AddEdge(adapters[index], participants[index], idempotent: false);
                    builder.AddEdge(participants[index], exits[index], idempotent: false);
                }
                builder.AddFanInBarrierEdge(exits, join);
            }
        );
    }

    private ExecutorBinding BindParticipant(PipelineBranch<TState> branch) =>
        branch.Participant.Descriptor switch
        {
            GeneratedOutcomeStepDescriptor<TState> outcome => outcome.Bind(
                new StandardOutcomeRouteAwareness<TState> { Matches = _ => true }
            ),
            GeneratedStateStepDescriptor<TState> state => state.Bind(),
            GeneratedPassThroughStepDescriptor<TState> passThrough => passThrough.Bind(),
            _ => throw new InvalidOperationException(
                $"Parallel branch '{branch.Id}' participant '{branch.Participant.Id}' is not a supported generated stage or agent."
            ),
        };

    private string PhysicalId(string role, int index, string participantId) =>
        $"{_id}--{role}-{index}--{participantId}";
}

internal sealed record ParallelGraphBinding(
    ExecutorBinding Entry,
    ExecutorBinding Exit,
    IReadOnlySet<string> PhysicalIds,
    Action<WorkflowBuilder> AddTo
);

internal sealed record ParallelPreparedMessage<TState>(
    string OccurrenceId,
    PipelineMessage<TState> Baseline,
    IReadOnlyList<PipelineMessage<TState>> Branches
) : IPipelineRunContextCarrier
{
    public PipelineRunContext? RunContext => Baseline.RunContext;
}

internal sealed record ParallelBranchContext<TState>(
    string GroupId,
    string OccurrenceId,
    string BranchId,
    int Index,
    PipelineMessage<TState> Baseline
);

internal sealed record ParallelBranchResult<TState>(
    string GroupId,
    string OccurrenceId,
    string BranchId,
    int Index,
    PipelineMessage<TState> Baseline,
    PipelineMessage<TState> Result
) : IPipelineRunContextCarrier
{
    public PipelineRunContext? RunContext => Baseline.RunContext;
}

internal sealed class ParallelForkExecutor<TState>(
    string id,
    Func<TState, TState> clone,
    IReadOnlyList<string> branchIds
)
    : Executor<PipelineMessage<TState>, ParallelPreparedMessage<TState>>(
        id + "--fork",
        options: null,
        declareCrossRunShareable: true
    )
{
    public override async ValueTask<ParallelPreparedMessage<TState>> HandleAsync(
        PipelineMessage<TState> input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        if (input.RunContext is { } runContext)
        {
            await runContext.BeginParallelAsync(id, cancellationToken);
        }

        var occurrenceId = input.Runtime.NextInvocationId(id);
        var runtime = input.Runtime.IncrementInvocations(id);
        var baseline = input with { Runtime = runtime };
        var branches = Enumerable
            .Range(0, branchIds.Count)
            .Select(index =>
                baseline with
                {
                    State = clone(baseline.State),
                    Runtime = runtime.Copy(),
                    ParallelContext = new ParallelBranchContext<TState>(
                        id,
                        occurrenceId,
                        branchIds[index],
                        index,
                        baseline
                    ),
                }
            )
            .ToArray();
        return new ParallelPreparedMessage<TState>(occurrenceId, baseline, branches);
    }
}

internal sealed class ParallelBranchAdapterExecutor<TState>(string id, int index)
    : Executor<ParallelPreparedMessage<TState>, PipelineMessage<TState>>(
        id,
        options: null,
        declareCrossRunShareable: true
    )
{
    public override ValueTask<PipelineMessage<TState>> HandleAsync(
        ParallelPreparedMessage<TState> input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(input.Branches[index]);
}

internal sealed class ParallelBranchExitExecutor<TState>(
    string id,
    string groupId,
    string branchId,
    int index
)
    : Executor<PipelineMessage<TState>, ParallelBranchResult<TState>>(
        id,
        options: null,
        declareCrossRunShareable: true
    )
{
    public override ValueTask<ParallelBranchResult<TState>> HandleAsync(
        PipelineMessage<TState> input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var branchContext =
            input.ParallelContext
            ?? throw new InvalidOperationException(
                $"Parallel branch '{branchId}' completed without fork correlation."
            );
        if (
            branchContext.GroupId != groupId
            || branchContext.BranchId != branchId
            || branchContext.Index != index
        )
        {
            throw new InvalidOperationException(
                $"Parallel branch '{branchId}' received invalid fork correlation."
            );
        }
        return ValueTask.FromResult(
            new ParallelBranchResult<TState>(
                groupId,
                branchContext.OccurrenceId,
                branchId,
                index,
                branchContext.Baseline,
                input with
                {
                    ParallelContext = null,
                }
            )
        );
    }
}

internal sealed class ParallelJoinExecutor<TState>
    : Executor<ParallelBranchResult<TState>, PipelineMessage<TState>?>
{
    private const string BatchKey = "parallel-branch-results";
    private readonly string _groupId;
    private readonly IReadOnlyList<string> _branchIds;
    private readonly Func<PipelineParallelMerge<TState>, TState> _merge;
    private readonly StandardOutcomeRouteAwareness<TState> _routeAwareness;

    public ParallelJoinExecutor(
        string id,
        IReadOnlyList<string> branchIds,
        Func<PipelineParallelMerge<TState>, TState> merge,
        StandardOutcomeRouteAwareness<TState> routeAwareness
    )
        : base(id, options: null, declareCrossRunShareable: true)
    {
        _groupId = id;
        _branchIds = branchIds;
        _merge = merge;
        _routeAwareness = routeAwareness;
    }

    protected override ValueTask OnMessageDeliveryStartingAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken = default
    ) =>
        context.QueueStateUpdateAsync(
            BatchKey,
            new List<ParallelBranchResult<TState>>(),
            cancellationToken
        );

    public override async ValueTask<PipelineMessage<TState>?> HandleAsync(
        ParallelBranchResult<TState> message,
        IWorkflowContext context,
        CancellationToken cancellationToken = default
    )
    {
        var batch = await context.ReadOrInitStateAsync(
            BatchKey,
            static () => new List<ParallelBranchResult<TState>>(),
            cancellationToken
        );
        batch.Add(message);
        await context.QueueStateUpdateAsync(BatchKey, batch, cancellationToken);
        return null;
    }

    protected override async ValueTask OnMessageDeliveryFinishedAsync(
        IWorkflowContext context,
        CancellationToken cancellationToken = default
    )
    {
        var batch = await context.ReadStateAsync<List<ParallelBranchResult<TState>>>(
            BatchKey,
            cancellationToken
        );
        if (batch is null || batch.Count == 0)
        {
            return;
        }
        if (
            batch.Count != _branchIds.Count
            || batch.Select(item => item.BranchId).Distinct(StringComparer.Ordinal).Count()
                != _branchIds.Count
        )
        {
            throw new InvalidOperationException(
                $"Parallel group '{_groupId}' did not receive exactly one result from every branch."
            );
        }
        if (batch.Select(item => item.OccurrenceId).Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new InvalidOperationException(
                $"Parallel group '{_groupId}' received mixed occurrence results."
            );
        }

        var ordered = batch.OrderBy(item => item.Index).ToArray();
        var baseline = ordered[0].Baseline;
        PipelineMessage<TState> output;
        var failed = ordered.FirstOrDefault(item =>
            item.Result.LatestOutcome?.Kind == StandardOutcomeKinds.Failed
        );
        if (failed is not null)
        {
            output = failed.Result with
            {
                Runtime = PipelineRuntime.Merge(
                    baseline.Runtime,
                    ordered.Select(item => item.Result.Runtime)
                ),
                LatestOutcome = new BlockOutcome(
                    StandardOutcomeKinds.Failed,
                    _groupId,
                    failed.Result.LatestOutcome!.Summary,
                    failed.Result.LatestOutcome.Payload
                ),
                LatestResult = PipelineResultPayload.Create(
                    _groupId,
                    nameof(Outcome<TState>.Failed),
                    failed.Result.LatestOutcome.Payload
                ),
                Status = PipelineRunStatus.Succeeded,
                ParallelContext = null,
            };
            output = output with
            {
                Status = _routeAwareness.Matches(output)
                    ? PipelineRunStatus.Succeeded
                    : PipelineRunStatus.Failed,
            };
        }
        else
        {
            var states = ordered.ToDictionary(
                item => item.BranchId,
                item => item.Result.State,
                StringComparer.Ordinal
            );
            var runtime = PipelineRuntime.Merge(
                baseline.Runtime,
                ordered.Select(item => item.Result.Runtime)
            );
            var mergedState = _merge(
                new PipelineParallelMerge<TState>(baseline.State, _branchIds, states)
            );
            output = baseline with
            {
                State = mergedState,
                Runtime = runtime,
                LatestOutcome = new BlockOutcome(
                    StandardOutcomeKinds.Success,
                    _groupId,
                    "Succeeded",
                    JsonSerializer.SerializeToElement(new { })
                ),
                LatestResult = PipelineResultPayload.Create(
                    _groupId,
                    nameof(Outcome<TState>.Success),
                    new { }
                ),
                Status = PipelineRunStatus.Succeeded,
                ParallelContext = null,
            };
        }

        if (baseline.RunContext is { } runContext)
        {
            var accepted = runContext.ShouldPersist(_groupId)
                ? failed is null
                    ? PipelineAcceptedValue.From(output.State)
                    : PipelineAcceptedValue.FromPayload<FailureEvidence>(
                        output.LatestOutcome!.Payload
                    )
                : null;
            await runContext.ObserveAsync(
                new PipelineStepCompleted(
                    runContext.RunId,
                    _groupId,
                    new PipelineRunOutcome(
                        output.LatestOutcome!.Kind,
                        _groupId,
                        output.LatestOutcome.Summary,
                        output.LatestOutcome.Payload,
                        output.LatestOutcome.Duration
                    ),
                    accepted
                ),
                cancellationToken
            );
            runContext.CompleteParallel(_groupId);
        }

        await context.SendMessageAsync(output, targetId: null, cancellationToken);
        await context.QueueStateUpdateAsync<List<ParallelBranchResult<TState>>>(
            BatchKey,
            null,
            cancellationToken
        );
    }
}
