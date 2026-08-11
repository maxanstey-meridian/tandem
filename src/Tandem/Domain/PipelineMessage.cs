using System.Text.Json;

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

    public PipelineMessage<TState> WithOutcome(BlockOutcome outcome) =>
        this with
        {
            LatestOutcome = outcome,
        };
}

internal sealed record PipelineResult(string StepId, string CaseId, JsonElement Payload);

internal sealed record PipelineRuntime(
    Guid RunId,
    IReadOnlyDictionary<string, JsonElement> AgentSessions,
    IReadOnlyDictionary<string, AgentUsage> AgentUsage,
    IReadOnlyDictionary<string, int> InvocationCounts,
    IReadOnlyDictionary<string, AgentProfileSelection> AgentProfiles,
    HashSet<string> GateLatches
)
{
    public static PipelineRuntime Create(Guid runId) =>
        new(
            runId,
            new Dictionary<string, JsonElement>(),
            new Dictionary<string, AgentUsage>(),
            new Dictionary<string, int>(),
            new Dictionary<string, AgentProfileSelection>(),
            new HashSet<string>(StringComparer.Ordinal)
        );

    public PipelineRuntime Copy() =>
        new(
            RunId,
            new Dictionary<string, JsonElement>(AgentSessions),
            new Dictionary<string, AgentUsage>(AgentUsage),
            new Dictionary<string, int>(InvocationCounts),
            new Dictionary<string, AgentProfileSelection>(AgentProfiles),
            new HashSet<string>(GateLatches, StringComparer.Ordinal)
        );

    public static PipelineRuntime Merge(
        PipelineRuntime baseline,
        IEnumerable<PipelineRuntime> branches
    )
    {
        var branchValues = branches.ToArray();
        if (branchValues.Any(branch => branch.RunId != baseline.RunId))
        {
            throw new InvalidOperationException(
                "Parallel runtime branches belong to different runs."
            );
        }
        return baseline with
        {
            AgentSessions = MergeDictionary(
                baseline.AgentSessions,
                branchValues.Select(value => value.AgentSessions),
                JsonElement.DeepEquals
            ),
            AgentUsage = MergeDictionary(
                baseline.AgentUsage,
                branchValues.Select(value => value.AgentUsage),
                EqualityComparer<AgentUsage>.Default.Equals
            ),
            InvocationCounts = MergeDictionary(
                baseline.InvocationCounts,
                branchValues.Select(value => value.InvocationCounts),
                EqualityComparer<int>.Default.Equals
            ),
            AgentProfiles = MergeDictionary(
                baseline.AgentProfiles,
                branchValues.Select(value => value.AgentProfiles),
                EqualityComparer<AgentProfileSelection>.Default.Equals
            ),
            GateLatches = MergeSet(
                baseline.GateLatches,
                branchValues.Select(value => value.GateLatches)
            ),
        };
    }

    private static IReadOnlyDictionary<string, TValue> MergeDictionary<TValue>(
        IReadOnlyDictionary<string, TValue> baseline,
        IEnumerable<IReadOnlyDictionary<string, TValue>> branches,
        Func<TValue, TValue, bool> equals
    )
    {
        var result = new Dictionary<string, TValue>(baseline, StringComparer.Ordinal);
        var changes = new Dictionary<string, (bool Present, TValue? Value)>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            foreach (var key in baseline.Keys.Concat(branch.Keys).Distinct(StringComparer.Ordinal))
            {
                var baselinePresent = baseline.TryGetValue(key, out var baselineValue);
                var branchPresent = branch.TryGetValue(key, out var branchValue);
                if (
                    baselinePresent == branchPresent
                    && (!baselinePresent || equals(baselineValue!, branchValue!))
                )
                {
                    continue;
                }
                if (
                    changes.TryGetValue(key, out var existing)
                    && (
                        existing.Present != branchPresent
                        || (branchPresent && !equals(existing.Value!, branchValue!))
                    )
                )
                {
                    throw new InvalidOperationException(
                        $"Parallel runtime branches made conflicting changes to '{key}'."
                    );
                }
                changes[key] = (branchPresent, branchValue);
            }
        }
        foreach (var (key, change) in changes)
        {
            if (change.Present)
            {
                result[key] = change.Value!;
            }
            else
            {
                result.Remove(key);
            }
        }
        return result;
    }

    private static HashSet<string> MergeSet(
        IReadOnlySet<string> baseline,
        IEnumerable<HashSet<string>> branches
    )
    {
        var result = new HashSet<string>(baseline, StringComparer.Ordinal);
        var changes = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var branch in branches)
        {
            foreach (var key in baseline.Concat(branch).Distinct(StringComparer.Ordinal))
            {
                var baselinePresent = baseline.Contains(key);
                var branchPresent = branch.Contains(key);
                if (baselinePresent == branchPresent)
                {
                    continue;
                }
                if (changes.TryGetValue(key, out var existing) && existing != branchPresent)
                {
                    throw new InvalidOperationException(
                        $"Parallel runtime branches made conflicting changes to gate latch '{key}'."
                    );
                }
                changes[key] = branchPresent;
            }
        }
        foreach (var (key, present) in changes)
        {
            if (present)
            {
                result.Add(key);
            }
            else
            {
                result.Remove(key);
            }
        }
        return result;
    }

    public string NextInvocationId(string stepId) =>
        $"{RunId:N}--{stepId}--{InvocationCounts.GetValueOrDefault(stepId) + 1}";

    public PipelineRuntime IncrementInvocations(string stepId) =>
        this with
        {
            InvocationCounts = new Dictionary<string, int>(InvocationCounts)
            {
                [stepId] = InvocationCounts.GetValueOrDefault(stepId) + 1,
            },
        };

    public PipelineRuntime WithSession(string stepId, JsonElement session) =>
        this with
        {
            AgentSessions = new Dictionary<string, JsonElement>(AgentSessions)
            {
                [stepId] = session,
            },
        };

    public PipelineRuntime WithoutSession(string stepId) =>
        this with
        {
            AgentSessions = AgentSessions
                .Where(kvp => kvp.Key != stepId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public PipelineRuntime WithUsage(string stepId, AgentUsage usage) =>
        this with
        {
            AgentUsage = new Dictionary<string, AgentUsage>(AgentUsage) { [stepId] = usage },
        };

    public PipelineRuntime WithoutUsage(string stepId) =>
        this with
        {
            AgentUsage = AgentUsage
                .Where(kvp => kvp.Key != stepId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public PipelineRuntime WithProfile(string stepId, AgentProfileSelection decision) =>
        this with
        {
            AgentProfiles = new Dictionary<string, AgentProfileSelection>(AgentProfiles)
            {
                [stepId] = decision,
            },
        };

    public PipelineRuntime WithoutProfile(string stepId) =>
        this with
        {
            AgentProfiles = AgentProfiles
                .Where(kvp => kvp.Key != stepId)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        };

    public bool IsGateLatched(string stepId, string gateId) =>
        GateLatches.Contains($"{stepId}:{gateId}");

    public PipelineRuntime WithGateLatch(string stepId, string gateId) =>
        this with
        {
            GateLatches = new HashSet<string>(GateLatches, StringComparer.Ordinal)
            {
                $"{stepId}:{gateId}",
            },
        };

    public PipelineRuntime WithoutGateLatch(string stepId, string gateId) =>
        this with
        {
            GateLatches = GateLatches
                .Where(key => !string.Equals(key, $"{stepId}:{gateId}", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal),
        };
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
    int CheckpointAtTokens,
    TimeSpan LastModelCallDuration,
    long CumulativeInputTokens = 0,
    long CumulativeOutputTokens = 0
);
