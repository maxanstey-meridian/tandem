using Tandem;

namespace Examples.Debate;

public sealed record DebateParticipants(
    OpenDebateStage Open,
    AgentDefinition<DebateState> Proposer,
    AgentDefinition<DebateState> Critic,
    AgentDefinition<DebateState> Judge,
    IPipelineNode<DebateState> Complete,
    IPipelineNode<DebateState> Failed
);

[PipelineStage("open")]
public sealed partial class OpenDebateStage
{
    public ValueTask ExecuteAsync(DebateState _, CancellationToken __) => ValueTask.CompletedTask;
}
