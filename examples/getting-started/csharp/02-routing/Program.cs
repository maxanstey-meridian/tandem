using Tandem;

namespace GettingStarted.Routing;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var classify = new ClassifyStage();
        var accepted = PipelineNodes.Complete(new ResultOutput("accepted"));
        var rejected = PipelineNodes.Complete(new ResultOutput("rejected"));
        var pipeline = Pipeline
            .Start(classify, "route-input")
            .Route(state => state.Accepted, classify, accepted, "accepted")
            .Route(state => !state.Accepted, classify, rejected, "rejected")
            .Build(accepted, rejected);
        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new ExampleState(args.FirstOrDefault() ?? "Hello")
        );

        Console.WriteLine(result.Outcome?.Summary);
    }
}

public sealed record ExampleState(string Value, bool Accepted = false);

[PipelineStage("classify")]
public sealed partial class ClassifyStage
{
    public ValueTask<ExampleState> ExecuteAsync(ExampleState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Accepted = state.Value.Length >= 3 });
}

public sealed class ResultOutput(string id) : IPipelineCompletion<ExampleState>
{
    public string Id => id;

    public string Summarize(ExampleState state) => $"{id}: {state.Value}";
}
