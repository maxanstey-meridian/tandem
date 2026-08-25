using Tandem;
using Tandem.Ledger;

namespace GettingStarted.Persistence;

public static class Program
{
    public static async Task Main()
    {
        var normalize = new NormalizeStage();
        var pipeline = Pipeline
            .Start(normalize, "persistent-normalization")
            .Persist()
            .Build(normalize);
        var ledgerPath = Path.GetFullPath("getting-started.sqlite3");
        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new ExampleState(" Hello "),
            new SqlitePipelineRunOptions(ledgerPath)
        );

        Console.WriteLine($"Run {result.RunId:N} recorded in {ledgerPath}");
    }
}

public sealed record ExampleState(string Value);

[PipelineStage("normalize")]
public sealed partial class NormalizeStage
{
    public ValueTask<ExampleState> ExecuteAsync(ExampleState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Value = state.Value.Trim().ToLowerInvariant() });
}
