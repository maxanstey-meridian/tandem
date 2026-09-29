using System.Text.Json;

namespace Tandem;

public enum ToolEffect
{
    Read,
    WorkspaceMutation,
    ProcessExecution,
    LifecycleTransition,
    Unclassified,
}

public enum ToolEvidence
{
    None,
    RepositoryInspection,
}

public enum ToolInvocationStatus
{
    Completed,
    Failed,
    Blocked,
    Faulted,
}

public abstract record ToolResultEvidence
{
    public sealed record Process(
        int ExitCode,
        string Stdout,
        string Stderr,
        TimeSpan Duration,
        bool TimedOut,
        bool Truncated
    ) : ToolResultEvidence;
}

public sealed record ToolObservation(string Name, ToolEffect Effect, ToolEvidence Evidence);

public sealed record ToolInvocationObservation(
    string Name,
    ToolEffect Effect,
    JsonElement Arguments,
    ToolInvocationStatus Status,
    ToolResultEvidence? Result
);
