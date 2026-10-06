# Getting Started

This page builds the smallest useful pipeline: one deterministic stage and one output. It
needs no model and no API key.

## Requirements

::: code-group

```text [C#]
.NET 10 SDK
```

```text [TypeScript]
Node.js 22 or newer
.NET 10 runtime
macOS on Apple silicon, or Linux x64
```

:::

TypeScript applications don't build .NET code. The npm package bundles the compiled bridge
for each supported platform, and only needs the .NET runtime to load it.

## Create the application

::: code-group

```sh [C#]
dotnet new console --framework net10.0 --name TandemQuickstart
cd TandemQuickstart
dotnet add package Meridian.Tandem
dotnet add package Meridian.Tandem.Generators
```

```sh [TypeScript]
mkdir tandem-quickstart
cd tandem-quickstart
npm init -y
npm install @maxanstey-meridian/tandem zod
```

:::

## Write the pipeline

::: code-group

```csharp [Program.cs]
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

```js [index.mjs]
import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";
import { z } from "zod";

const State = z.object({ value: z.string() });
const normalize = stage({
  id: "normalize",
  execute: (state) => ({ ...state, value: state.value.trim().toLowerCase() }),
});
const done = output({ id: "done", summary: (state) => state.value });
const normalizeInput = pipeline({
  name: "normalize-input",
  state: State,
  nodes: [normalize, done],
  start: normalize,
  routes: [route({ from: normalize, to: done, label: "normalized" })],
  outputs: [done],
});

const result = await run(normalizeInput, { value: "  Hello Tandem  " });
console.log(result.summary);
```

:::

## Run it

::: code-group

```sh [C#]
dotnet run
```

```sh [TypeScript]
node index.mjs
```

:::

Both print `hello tandem`.

The typed state owns the facts, the stage owns one deterministic operation, and the route owns
the decision to continue to `done`. Every Tandem application is that same idea at a larger
scale.

::: tip TypeScript and ESM
The package is ESM-only, and `npm init` creates a CommonJS project, which is why the file is
`index.mjs`. To use `.ts` files, mark the project as ESM with `npm pkg set type=module`.
Node.js 22.18 or newer runs TypeScript directly by stripping types. On older Node.js 22
releases, use `npx tsx index.mts`.
:::

## Next

- Read [The Mental Model](/guides/mental-model): the handful of pieces every pipeline is made
  of
- Add a model-backed participant with [Agents](/guides/agents)
- Work through the getting-started progression in
  [C#](https://github.com/maxanstey-meridian/tandem/tree/main/examples/getting-started) or
  [TypeScript](https://github.com/maxanstey-meridian/tandem-ts/tree/main/examples/getting-started),
  from one participant through routing, stages and persistence
