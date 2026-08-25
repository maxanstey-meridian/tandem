# Meridian.Tandem.Terminal

Optional observation-driven terminal presentation for Tandem pipelines.

```sh
dotnet add package Meridian.Tandem.Terminal --version 0.1.0-alpha.1
```

```csharp
using Tandem;
using Tandem.Terminal;

var result = await new PipelineRunner().RunWithTerminalAsync(
    pipeline,
    initialState,
    cancellationToken: cancellationToken);
```

The terminal is a host concern layered over Tandem observations. It does not own pipeline state or
control flow.
