using Tandem;

namespace GettingStarted.Stage;

public static class Program
{
    public static async Task Main()
    {
        var normalize = new NormalizeStage();
        var measure = new MeasureStage();
        var done = PipelineNodes.Complete(new DoneOutput());
        var pipeline = Pipeline
            .Start(normalize, "normalize-and-measure")
            .Route(_ => true, normalize, measure, "normalized")
            .Route(_ => true, measure, done, "measured")
            .Build(done);
        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new ExampleState(" Hello world ")
        );

        Console.WriteLine(result.Outcome?.Summary);
    }
}

public sealed record ExampleState(string Value, int Length = 0);

[PipelineStage("normalize")]
public sealed partial class NormalizeStage
{
    public ValueTask<ExampleState> ExecuteAsync(ExampleState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Value = state.Value.Trim().ToLowerInvariant() });
}

[PipelineStage("measure")]
public sealed partial class MeasureStage
{
    public ValueTask<ExampleState> ExecuteAsync(ExampleState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Length = state.Value.Length });
}

public sealed class DoneOutput : IPipelineCompletion<ExampleState>
{
    public string Id => "done";

    public string Summarize(ExampleState state) => $"{state.Value} ({state.Length} characters)";
}
