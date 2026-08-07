namespace Tandem.Sample.Debate;

public sealed class DebateComposition(DebateSteps debate)
{
    public Pipeline Build() =>
        TandemWorkflow
            .Start(
                at: debate.Open,
                name: "debate",
                description: "Revise an argument until a critic accepts it, then judge it."
            )
            .Route(on: debate.Open.Result.Opened, to: debate.Proposer, label: "debate opened")
            .Route(
                on: debate.Proposer.Result.Proposed,
                to: debate.Critic,
                label: "argument proposed"
            )
            .Route(
                on: debate.Critic.Result.RevisionRequested,
                to: debate.Proposer,
                label: "revision requested"
            )
            .Route(on: debate.Critic.Result.Accepted, to: debate.Judge, label: "argument accepted")
            .Route(
                on: debate.Judge.Result.VerdictSubmitted,
                to: debate.Complete,
                label: "verdict submitted"
            )
            .Build(debate.Complete);
}
