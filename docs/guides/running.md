# Running a Pipeline

Your application owns the process. It starts a pipeline with an initial state and gets back a
result.

::: code-group

```csharp [C#]
var result = await new PipelineRunner().RunAsync(
    // Run this configured lifecycle...
    codeWriter,
    // ...starting from these application facts...
    initialState,
    // ...recording accepted values to this ledger...
    new SqlitePipelineRunOptions("code-writer.sqlite3"),
    // ...until completion or caller cancellation.
    cancellationToken);

Console.WriteLine(result.Status);
Console.WriteLine(result.State);
```

```ts [TypeScript]
import { run } from "@maxanstey-meridian/tandem";

const result = await run(codeWriter, initialState, {
  // Let the caller cancel a run that takes too long.
  signal: AbortSignal.timeout(180_000),
  // Supply a ledger only when this pipeline persists values.
  ledgerPath: "code-writer.sqlite3",
});

console.log(result.succeeded);
console.log(result.state);
```

:::

The result carries the final state, whether the run reached a successful or a failed output,
and that output's summary.

## Run options

| Concern | C# | TypeScript |
|---|---|---|
| Cancellation | `CancellationToken` | `signal: AbortSignal` |
| Ledger | `SqlitePipelineRunOptions(path)` | `ledgerPath` |
| Interaction handlers | `PipelineRunOptions(Interactions: ...)` | `interactions` |
| Live observation | `PipelineRunOptions(Observer: ...)` | `observe: (event) => ...` |
| Terminal view | `RunWithTerminalAsync(...)` | `presentation: "terminal"` |
| Ledger tools for agents | `EnableLedgerTools = true` | `enableLedgerTools: true` |

`SqlitePipelineRunOptions` accepts the same `Interactions` and `Observer` as
`PipelineRunOptions`.

## Watching a run

Observations stream as the run happens: steps starting, completing and faulting, agent text and
reasoning, the model selected, token usage, and rejected structured output.

In TypeScript, pass `observe` a callback. In C#, pass an `IPipelineObserver`, and compose
several with `PipelineObservers.Compose(first, second)`.

### The terminal view

![The Tandem terminal view](/tui-screenshot.png)

`Meridian.Tandem.Terminal` renders a run live in the terminal:

::: code-group

```csharp [C#]
using Tandem.Terminal;

var result = await new PipelineRunner().RunWithTerminalAsync(
    pipeline,
    initialState,
    cancellationToken: cancellationToken);
```

```ts [TypeScript]
const result = await run(pipeline, initialState, { presentation: "terminal" });
```

:::

When output is redirected or non-interactive, it prints the same transcript as plain text. The
terminal is a host concern layered over observations. It doesn't own state or control flow.

## Failure modes

A run ends in one of four ways:

- **Succeeded** or **failed**: it reached one of its declared outputs. A failed output is an
  ordinary, routed result, not an exception.
- **Cancelled**: the caller cancelled it.
- **Faulted**: something outside the graph's declared routes went wrong.

In TypeScript, cancellation throws `TandemCancellationError`, a fault throws
`TandemRuntimeError`, and a contract problem throws `ContractValidationError`, which lists each
problem's path and message. All of them extend `TandemError`.

## Processes own runs

A run lives in the process that started it. Persistence records what the run accepted, but it
can't reopen or resume a run. See [Persistence](/guides/persistence).
