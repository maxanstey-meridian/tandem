using Tandem;

namespace GettingStarted.SinglePipeline;

public static class Program
{
    public static async Task Main()
    {
        var normalize = new NormalizeStage();
        var pipeline = Pipeline.Start(normalize, "single-pipeline").Build(normalize);
        var result = await new PipelineRunner().RunAsync(pipeline, new ExampleState(" Hello "));

        Console.WriteLine(result.State.Value);
    }
}

public sealed record ExampleState(string Value);

[PipelineStage("normalize")]
public sealed partial class NormalizeStage
{
    public ValueTask<ExampleState> ExecuteAsync(ExampleState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Value = state.Value.Trim() });
}
