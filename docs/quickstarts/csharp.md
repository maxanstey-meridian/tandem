# C# Quickstart

## Requirements

- .NET 10 SDK

## Create the application

```sh
dotnet new console --framework net10.0 --name TandemQuickstart
cd TandemQuickstart
dotnet add package Meridian.Tandem --version 0.1.0-alpha.1
dotnet add package Meridian.Tandem.Generators --version 0.1.0-alpha.1
```

Replace `Program.cs` with:

```csharp
using Tandem;

var normalize = new NormalizeStage();
var done = PipelineNodes.Complete(new DoneOutput());
var pipeline = Pipeline
    .Start(normalize, "normalize-input")
    .Route(_ => true, normalize, done, "normalized")
    .Build(done);
var result = await new PipelineRunner().RunAsync(
    pipeline,
    new InputState("  Hello Tandem  "));

Console.WriteLine(result.Outcome?.Summary);

public sealed record InputState(string Value);

[PipelineStage("normalize")]
public sealed partial class NormalizeStage
{
    public ValueTask<InputState> ExecuteAsync(InputState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Value = state.Value.Trim().ToLowerInvariant() });
}

public sealed class DoneOutput : IPipelineCompletion<InputState>
{
    public string Id => "done";

    public string Summarize(InputState state) => state.Value;
}
```

Run it:

```sh
dotnet run
```

The typed state owns the facts, `NormalizeStage` owns one deterministic operation, and the route owns
the decision to continue to `done`. Continue with the package-backed
[getting-started progression](../../examples/getting-started).
