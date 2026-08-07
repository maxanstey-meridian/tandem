namespace Tandem.Sample.Debate;

public sealed class DebateComposition(DebateStepsFactory stepsFactory)
{
    public Pipeline Build(PipelineBuildContext context)
    {
        var debate = stepsFactory.Create(context);
        return TandemWorkflow
            .Start(
                at: debate.Open,
                name: "debate",
                description: "Revise an argument until a critic accepts it, then judge it."
            )
            .Route(on: debate.Open, to: debate.Proposer, label: "debate opened")
            .Route(
                on: debate.Proposer.Result.Success,
                to: debate.Critic,
                label: "argument proposed"
            )
            .Route(
                on: debate.Critic.Result.RevisionRequested,
                to: debate.Proposer,
                label: "revision requested"
            )
            .Route(on: debate.Critic.Result.Accepted, to: debate.Judge, label: "argument accepted")
            .Route(on: debate.Critic.Result.Failed, to: debate.Failed, label: "agent failed")
            .Route(on: debate.Judge.Result.Success, to: debate.Complete, label: "verdict submitted")
            .Build(debate.Complete, debate.Failed);
    }
}
