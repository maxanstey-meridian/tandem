namespace Tandem;

public sealed record PipelineInspection(
    string Name,
    string? Description,
    string StartStepId,
    IReadOnlyList<string> StepIds,
    IReadOnlyList<PipelineInteractionInspection> Interactions,
    IReadOnlyList<PipelineRouteInspection> Routes,
    IReadOnlyList<string> OutputStepIds,
    IReadOnlyList<string> PersistentStepIds
)
{
    public IReadOnlyList<PipelineParallelInspection> ParallelGroups { get; init; } = [];
    public IReadOnlyList<PipelineCollectionInspection> Collections { get; init; } = [];

    /// <summary>Renders the inspected routes, including parallel fan-out and fan-in, as a Mermaid flowchart.</summary>
    public string ToMermaid()
    {
        var aliases = StepIds
            .Select((id, index) => (id, alias: $"n{index}"))
            .ToDictionary(item => item.id, item => item.alias, StringComparer.Ordinal);
        var parallelRoutes = ParallelGroups.SelectMany(group =>
            group.Branches.SelectMany(branch =>
                new[]
                {
                    new PipelineRouteInspection(
                        group.Id,
                        branch.ParticipantId,
                        Conditional: false,
                        branch.Id
                    ),
                    new PipelineRouteInspection(branch.ParticipantId, group.Id, Conditional: false),
                }
            )
        );
        var lines = new List<string> { "flowchart TD" };
        lines.AddRange(
            StepIds.Select(id =>
            {
                var label = $"\"{Escape(id)}\"";
                return id == StartStepId ? $"    {aliases[id]}(({label}))"
                    : OutputStepIds.Contains(id, StringComparer.Ordinal)
                        ? $"    {aliases[id]}{{{{{label}}}}}"
                    : $"    {aliases[id]}[{label}]";
            })
        );
        lines.AddRange(
            Routes
                .Concat(parallelRoutes)
                .Select(route =>
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

    private static string Escape(string value) =>
        value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
}

public sealed record PipelineCollectionInspection(
    string Id,
    int Max,
    IReadOnlyList<string> AgentIds
);

public sealed record PipelineParallelInspection(
    string Id,
    IReadOnlyList<PipelineParallelBranchInspection> Branches
);

public sealed record PipelineParallelBranchInspection(string Id, int Index, string ParticipantId);

public sealed record PipelineRouteInspection(
    string SourceId,
    string TargetId,
    bool Conditional,
    string? Label = null
);

public sealed record PipelineInteractionInspection(
    string Id,
    string RequestType,
    string ResponseType
);
