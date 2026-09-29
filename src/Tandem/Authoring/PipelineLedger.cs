using Microsoft.Extensions.AI;

namespace Tandem;

/// <summary>
/// What a run's ledger contributes to its agents when a host opts in: the ledger tools (named and
/// classified in <see cref="Infrastructure.BuiltInAgentTools.Ledger"/>) and the entry under which
/// an action's process output was journaled.
/// </summary>
internal interface IPipelineLedger
{
    public IReadOnlyList<AITool> Tools { get; }

    public ValueTask<long?> FindActionEntryAsync(
        string stepId,
        string invocationId,
        CancellationToken cancellationToken = default
    );
}
