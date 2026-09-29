using System.Collections.Immutable;
using System.Text.Json;
using Tandem.Infrastructure;

namespace Tandem.Domain;

internal interface IOutcomeBearingMessage
{
    public BlockOutcome? LatestOutcome { get; }
}

internal sealed record PipelineMessage<TState>(
    PipelineRuntime Runtime,
    TState State,
    BlockOutcome? LatestOutcome = null,
    PipelineResult? LatestResult = null,
    PipelineRunStatus Status = PipelineRunStatus.Succeeded
) : IOutcomeBearingMessage, IPipelineRunContextCarrier
{
    internal PipelineRunContext? RunContext { get; init; }
    internal ParallelBranchContext<TState>? ParallelContext { get; init; }
    PipelineRunContext? IPipelineRunContextCarrier.RunContext => RunContext;
}

internal sealed record PipelineResult(string StepId, string CaseId, JsonElement Payload);

internal sealed record AgentStepRuntime(
    JsonElement? Session,
    IReadOnlyList<ToolInvocationObservationDescriptor> ToolInvocations,
    AgentUsage? Usage,
    AgentProfileSelection? Profile,
    ImmutableHashSet<string> Latches,
    int Count
)
{
    public static AgentStepRuntime Empty { get; } =
        new(null, [], null, null, ImmutableHashSet.Create<string>(StringComparer.Ordinal), 0);

    public AgentStepRuntime WithoutConversation() =>
        this with
        {
            Session = null,
            ToolInvocations = [],
            Usage = null,
        };

    public bool SameAs(AgentStepRuntime other) =>
        ReferenceEquals(this, other)
        || (
            Count == other.Count
            && Equals(Usage, other.Usage)
            && Equals(Profile, other.Profile)
            && Latches.SetEquals(other.Latches)
            && ReferenceEquals(ToolInvocations, other.ToolInvocations)
            && (
                Session is { } session
                    ? other.Session is { } otherSession
                        && JsonElement.DeepEquals(session, otherSession)
                    : other.Session is null
            )
        );
}

internal sealed record PipelineRuntime(
    Guid RunId,
    ImmutableDictionary<string, AgentStepRuntime> Steps
)
{
    public string? InvocationScope { get; init; }
    public string? ObservationVisitId { get; init; }

    public static PipelineRuntime Create(Guid runId) =>
        new(runId, ImmutableDictionary.Create<string, AgentStepRuntime>(StringComparer.Ordinal));

    public AgentStepRuntime Step(string stepId) =>
        Steps.GetValueOrDefault(stepId) ?? AgentStepRuntime.Empty;

    public PipelineRuntime WithStep(
        string stepId,
        Func<AgentStepRuntime, AgentStepRuntime> update
    ) => this with { Steps = Steps.SetItem(stepId, update(Step(stepId))) };

    public static PipelineRuntime Merge(
        PipelineRuntime baseline,
        IEnumerable<PipelineRuntime> branches
    )
    {
        var steps = baseline.Steps;
        var changed = new Dictionary<string, AgentStepRuntime?>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            if (branch.RunId != baseline.RunId)
            {
                throw new InvalidOperationException(
                    "Parallel runtime branches belong to different runs."
                );
            }
            foreach (var stepId in baseline.Steps.Keys.Union(branch.Steps.Keys))
            {
                var before = baseline.Steps.GetValueOrDefault(stepId);
                var after = branch.Steps.GetValueOrDefault(stepId);
                if (Same(before, after))
                {
                    continue;
                }
                if (changed.TryGetValue(stepId, out var existing) && !Same(existing, after))
                {
                    throw new InvalidOperationException(
                        $"Parallel runtime branches made conflicting changes to '{stepId}'."
                    );
                }
                changed[stepId] = after;
                steps = after is null ? steps.Remove(stepId) : steps.SetItem(stepId, after);
            }
        }
        return baseline with { Steps = steps };

        static bool Same(AgentStepRuntime? left, AgentStepRuntime? right) =>
            left is null ? right is null : right is not null && left.SameAs(right);
    }

    public string NextInvocationId(string stepId) =>
        $"{InvocationScope ?? RunId.ToString("N")}--{stepId}--{Step(stepId).Count + 1}";

    public PipelineRuntime IncrementInvocations(string stepId) =>
        WithStep(stepId, step => step with { Count = step.Count + 1 });

    public bool IsGateLatched(string stepId, string gateId) =>
        Step(stepId).Latches.Contains(gateId);
}

internal sealed record BlockOutcome(
    string Kind,
    string StepId,
    string Summary,
    JsonElement Payload = default,
    TimeSpan Duration = default
);

internal sealed record AgentUsage(
    int CurrentInputTokens,
    int CurrentOutputTokens,
    int CurrentContextTokens,
    int ContextWindowTokens,
    long CumulativeInputTokens = 0,
    long CumulativeOutputTokens = 0
);
