# Routes & Outputs

Participants don't choose their successors. Routes do.

A route has:

- a source
- a destination
- a semantic label
- optionally, a standard execution outcome
- optionally, a predicate over typed state

## Outcomes are not decisions

`Success` and `Failed` say whether a participant **executed** successfully. They aren't a
catalogue of domain outcomes such as `Approved`, `Rejected`, `ChangesRequested` or
`Escalated`. Domain decisions belong in state, and routes read them.

So a reviewer that requests changes has *succeeded*. It produced a valid decision, and a route
sends that decision back around. A reviewer whose model call faulted has *failed*, and a
different route handles that.

## Code Writer's routes

::: code-group

```csharp [C#]
public Pipeline<CodeWriterState> Build() =>
    Pipeline
        // Begin with the implementer and name the whole lifecycle.
        .Start(
            at: codeWriter.Implementer,
            name: "code-writer",
            description: "Implement and verify a function until review accepts it.")
        // A completed capability call gives verification a candidate to check.
        .Route(on: codeWriter.Implementer.Success, to: codeWriter.Verification, label: "implementation submitted")
        // If the implementer itself fails, there's no candidate to verify.
        .Route(on: codeWriter.Implementer.Failed, to: codeWriter.Failed, label: "implementer failed")
        // Passing checks move the exact candidate and evidence to review.
        .Route(
            from: codeWriter.Verification,
            when: state => state.Verification?.Passed is true,
            to: codeWriter.Reviewer,
            label: "verification passed")
        // Failed checks send their evidence back to the same implementer.
        .Route(
            from: codeWriter.Verification,
            when: state => state.Verification?.Passed is false,
            to: codeWriter.Implementer,
            label: "verification failed")
        // Requested changes are valid output, so they loop rather than fail.
        .Route(
            on: codeWriter.Reviewer.Success,
            when: state => state.Review?.Decision == ReviewDisposition.RequestChanges,
            to: codeWriter.Implementer,
            label: "changes requested")
        // Accept is the application fact that finishes the work.
        .Route(
            on: codeWriter.Reviewer.Success,
            when: state => state.Review?.Decision == ReviewDisposition.Accept,
            to: codeWriter.Complete,
            label: "accepted")
        // A reviewer fault is different from a RequestChanges decision.
        .Route(on: codeWriter.Reviewer.Failed, to: codeWriter.Failed, label: "reviewer failed")
        // Keep accepted values so this run can be inspected later.
        .Persist()
        // A run can leave the graph only through these two outputs.
        .Build(codeWriter.Complete, codeWriter.Failed);
```

```ts [TypeScript]
return pipeline({
  // Give the whole lifecycle one name in logs and the ledger.
  name: "code-writer",
  // Every node reads and returns this same state shape.
  state: State,
  // List everything that can take a turn or finish the run.
  nodes: [implementer, verification, reviewer, done, failed],
  // The implementer receives the initial state first.
  start: implementer,

  routes: [
    // A completed capability call gives verification a candidate to check.
    route({ from: implementer, to: verification, outcome: "success", label: "implementation submitted" }),
    // If the implementer itself fails, there's no candidate to verify.
    route({ from: implementer, to: failed, outcome: "failed", label: "implementer failed" }),
    // Passing checks move the exact candidate and evidence to review.
    route({
      from: verification,
      to: reviewer,
      when: (state) => state.verification?.passed === true,
      label: "verification passed",
    }),
    // Failed checks send their evidence back to the same implementer.
    route({
      from: verification,
      to: implementer,
      when: (state) => state.verification?.passed === false,
      label: "verification failed",
    }),
    // Requested changes are valid output, so they loop rather than fail.
    route({
      from: reviewer,
      to: implementer,
      outcome: "success",
      when: (state) => state.review?.decision === "RequestChanges",
      label: "changes requested",
    }),
    // Accept is the application fact that finishes the work.
    route({
      from: reviewer,
      to: done,
      outcome: "success",
      when: (state) => state.review?.decision === "Accept",
      label: "accepted",
    }),
    // A reviewer fault is different from a RequestChanges decision.
    route({ from: reviewer, to: failed, outcome: "failed", label: "reviewer failed" }),
  ],

  // These are the only places the run may finish.
  outputs: [done, failed],
  // Keep accepted values so this run can be inspected later.
  persist: true,
});
```

:::

The two authoring surfaces describe the same machine.

## Evaluation rules

- A participant's routes are evaluated **in the order you declared them**, and the first match
  wins.
- Use an unconditional route for plain serial flow, and a `when` predicate for branching on
  state.
- Don't mix unconditional and outcome-specific routes from the same source. A second
  unconditional route for the same outcome would never be reached, so Tandem rejects it.

## Outputs

A successful output and a failed output are distinct, inspectable destinations, not implicit
conventions. A pipeline declares every output through which a run may finish.

::: code-group

```csharp [C#]
// Accepted reviews finish here.
var complete = PipelineNodes.Complete(new CodeWriterComplete());

// Agent faults finish somewhere explicitly unsuccessful.
var failed = PipelineNodes.Failed(new CodeWriterFailed());
```

```ts [TypeScript]
const done = output<State>({
  // Accepted reviews route to this successful endpoint.
  id: "done",
  // Return the reviewer's own concise account to the caller.
  summary: (state) => state.review!.summary,
});

const failed = output<State>({
  // Agent faults route to a separate endpoint.
  id: "failed",
  // Reaching this output means the run failed.
  failed: true,
  // Give the caller a useful result without exposing runtime internals.
  summary: () => "An agent failed before the code could be accepted.",
});
```

:::

In C#, an output is a class implementing `IPipelineCompletion<TState>` or
`IPipelineFailure<TState>`, with an `Id` and a `Summarize(state)` method.

## Inspecting the graph

Both surfaces can describe the graph they built, as nodes and routes, without running it.
Use `pipeline.Inspect()` in C# and `inspectPipeline(pipeline)` in TypeScript. In C#,
`inspection.ToMermaid()` renders it as a Mermaid diagram.
