# Meridian.Tandem

Typed, in-process agentic pipelines built on Microsoft Agent Framework.

```sh
dotnet add package Meridian.Tandem --version 0.1.0-alpha.1
dotnet add package Meridian.Tandem.Generators --version 0.1.0-alpha.1
```

```csharp
using Tandem;

var normalize = new NormalizeStage();
var pipeline = Pipeline.Start(normalize, "normalize-input").Build(normalize);
var result = await new PipelineRunner().RunAsync(pipeline, new InputState(" Hello "));

public sealed record InputState(string Value);

[PipelineStage("normalize")]
public sealed partial class NormalizeStage
{
    public ValueTask<InputState> ExecuteAsync(InputState state, CancellationToken _) =>
        ValueTask.FromResult(state with { Value = state.Value.Trim() });
}
```

State holds application facts, participants perform work, and routes decide what runs next. See the
[Tandem repository](https://github.com/maxanstey-meridian/tandem) for agents, generated stages,
capabilities, interactions, persistence, and complete examples.
