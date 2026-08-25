using Tandem;

namespace Examples.Debate;

public sealed class DebateComposition(DebateParticipants debate)
{
    public Pipeline<DebateState> Build()
    {
        return Pipeline
            .Start(
                at: debate.Open,
                name: "debate",
                description: "Revise an argument until a critic accepts it, then judge it."
            )
            .Route(on: debate.Open, to: debate.Proposer, label: "debate opened")
            .Route(on: debate.Proposer.Success, to: debate.Critic, label: "argument proposed")
            .Route(on: debate.Proposer.Failed, to: debate.Failed, label: "agent failed")
            .Route(
                on: debate.Critic.Success,
                when: state => state.CritiqueAccepted is false,
                to: debate.Proposer,
                label: "revision requested"
            )
            .Route(
                on: debate.Critic.Success,
                when: state => state.CritiqueAccepted is true,
                to: debate.Judge,
                label: "argument accepted"
            )
            .Route(on: debate.Critic.Failed, to: debate.Failed, label: "agent failed")
            .Route(on: debate.Judge.Success, to: debate.Complete, label: "verdict submitted")
            .Route(on: debate.Judge.Failed, to: debate.Failed, label: "agent failed")
            .Build(debate.Complete, debate.Failed);
    }
}
