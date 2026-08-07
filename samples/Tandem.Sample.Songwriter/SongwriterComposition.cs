namespace Tandem.Sample.Songwriter;

public sealed class SongwriterComposition(SongwriterStepsFactory stepsFactory)
{
    public Pipeline Build(PipelineBuildContext context)
    {
        var song = stepsFactory.Create(context);
        return TandemWorkflow
            .Start(
                at: song.Songwriter,
                name: "songwriter",
                description: "Write, lint, and proofread a song until it is accepted."
            )
            .Route(on: song.Songwriter.Result.Success, to: song.Lint, label: "song written")
            .Route(on: song.Lint.Result.Passed, to: song.Proofreader, label: "lint passed")
            .Route(on: song.Lint.Result.Failed, to: song.Songwriter, label: "lint failed")
            .Route(on: song.Proofreader.Result.Accepted, to: song.Complete, label: "proof accepted")
            .Route(
                on: song.Proofreader.Result.ChangesRequested,
                to: song.Songwriter,
                label: "changes requested"
            )
            .Route(on: song.Proofreader.Result.Failed, to: song.Failed, label: "agent failed")
            .Build(song.Complete, song.Failed);
    }
}
