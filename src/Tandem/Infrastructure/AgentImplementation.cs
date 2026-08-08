using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Tandem.Infrastructure;

internal sealed record AgentImplementationContext(
    string Id,
    IChatClient ChatClient,
    ChatOptions ChatOptions,
    string? WorkspacePath,
    bool ExposeWorkspaceMutationTools,
    ToolEffectRegistry ToolEffects
);

internal enum ToolEffect
{
    Read,
    WorkspaceMutation,
    LifecycleTransition,
}

internal enum ToolEvidence
{
    None,
    RepositoryInspection,
}

internal sealed record ToolSemantics(ToolEffect Effect, ToolEvidence Evidence = ToolEvidence.None);

internal sealed record ToolObservationDescriptor(string Name, ToolSemantics? Semantics);

internal sealed class ToolEffectRegistry
{
    private readonly Dictionary<string, ToolSemantics> _semantics = new(StringComparer.Ordinal);

    internal void Add(string name, ToolEffect effect, ToolEvidence evidence = ToolEvidence.None)
    {
        if (!_semantics.TryAdd(name, new ToolSemantics(effect, evidence)))
        {
            throw new InvalidOperationException(
                $"Tool '{name}' has more than one authority classification."
            );
        }
    }

    internal bool TryGet(string name, out ToolSemantics semantics) =>
        _semantics.TryGetValue(name, out semantics!);
}

internal delegate AIAgent AgentImplementationFactory(AgentImplementationContext context);

internal static class GenericAgentInstructions
{
    internal const string Value =
        "You are one bounded node in a Tandem pipeline. Follow the authored instructions, "
        + "use only the capabilities provided for this invocation, produce the requested result, "
        + "and return control to Tandem. A capability transition occurs only when Tandem reports acceptance.";
}
