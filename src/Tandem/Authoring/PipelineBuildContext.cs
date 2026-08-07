using Microsoft.Agents.AI;
using Tandem.Infrastructure.Projection;

namespace Tandem;

public sealed record PipelineBuildContext(
    Action<string, Guid, AgentResponseUpdate>? AgentUpdate = null,
    IBlockExecutionObserver? ExecutionObserver = null
);
