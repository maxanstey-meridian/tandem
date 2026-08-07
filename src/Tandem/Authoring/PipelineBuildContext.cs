namespace Tandem;

public sealed record PipelineBuildContext(
    Action<string, Guid, AgentUpdate>? AgentUpdate = null,
    IBlockExecutionObserver? ExecutionObserver = null
);
