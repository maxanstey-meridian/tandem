namespace Tandem.Sample.Songwriter;

public sealed class SongwriterComposition(SongwriterParticipants song)
{
    public Pipeline<SongwriterState> Build()
    {
        return Pipeline
            .Start(
                at: song.Songwriter,
                name: "songwriter",
                description: "Write, lint, and proofread a song until it is accepted."
            )
            .Route(on: song.Songwriter.Success, to: song.Lint, label: "song written")
            .Route(
                when: state => state.LintFeedback is null,
                from: song.Lint,
                to: song.Proofreader,
                label: "lint passed"
            )
            .Route(
                when: state => state.LintFeedback is not null,
                from: song.Lint,
                to: song.Songwriter,
                label: "lint failed"
            )
            .Route(
                on: song.Proofreader.Success,
                when: state => state.ProofreaderAccepted is true,
                to: song.Complete,
                label: "proof accepted"
            )
            .Route(
                on: song.Proofreader.Success,
                when: state => state.ProofreaderAccepted is false,
                to: song.Songwriter,
                label: "changes requested"
            )
            .Route(on: song.Proofreader.Failed, to: song.Failed, label: "agent failed")
            .Build(song.Complete, song.Failed);
    }
}
