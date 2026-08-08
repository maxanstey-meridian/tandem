using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Tandem.Infrastructure;

internal sealed record AgentImplementationContext(
    string Id,
    IChatClient ChatClient,
    ChatOptions ChatOptions,
    string? WorkspacePath,
    bool AllowMutation,
    bool HasToolInterceptor,
    bool IsCheckpointOnly
);

internal delegate AIAgent AgentImplementationFactory(AgentImplementationContext context);

internal static class GenericAgentInstructions
{
    internal const string Value =
        "You are one bounded node in a Tandem pipeline. Follow the authored instructions, "
        + "use only the capabilities provided for this invocation, produce the requested result, "
        + "and return control to Tandem. A capability transition occurs only when Tandem reports acceptance.";
}
