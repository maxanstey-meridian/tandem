namespace Tandem;

public sealed record PipelineBuildContext(IBlockExecutionObserver? ExecutionObserver = null);
