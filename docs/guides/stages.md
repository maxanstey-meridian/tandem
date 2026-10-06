# Stages

A stage is a deterministic participant. It uses the same state as agents and is routed in
exactly the same graph.

Code Writer's verification is a stage, because testing the submitted implementation doesn't
need another model:

::: code-group

```csharp [C#]
[PipelineStage("verification")]
public sealed partial class VerificationStage
{
    // The stage owns the normal C# service that performs the check.
    private readonly ImplementationAssessment _assessment = new();

    public async ValueTask<CodeWriterState> ExecuteAsync(
        CodeWriterState state,
        CancellationToken cancellationToken)
    {
        // Verification can't run until an implementation has been accepted.
        var source =
            state.Implementation?.Source
            ?? throw new InvalidOperationException("Verification requires an implementation.");

        // Run the check without involving another model.
        var verification = await _assessment.AssessAsync(source, cancellationToken);

        // Return the same state with the new evidence recorded.
        return state.RecordVerification(verification);
    }
}
```

```ts [TypeScript]
const verification = stage<State>({
  // Routes refer to this check by a stable name.
  id: "verification",

  execute: async (state) =>
    recordVerification(
      state,
      // Run ordinary code and put its evidence back into state.
      await assessImplementation(state.implementation!.source),
    ),
});
```

:::

A stage can do whatever belongs at that point in the lifecycle: validation, calculation,
database work, an API call, compilation, verification or a transformation. It doesn't need to
know who runs before or after it.

## C# stage shapes

In C#, `[PipelineStage("id")]` on a `partial` class lets the source generator in
`Meridian.Tandem.Generators` produce the stage plumbing. The generator reads your
`ExecuteAsync` signature:

- return `ValueTask` to pass state through unchanged
- return `ValueTask<TState>` to update state
- return `ValueTask<Outcome<TState>>` to report `Success` or `Failed` yourself

Use only the standard `Success` and `Failed` outcomes. Put domain decisions in state and
route on them.

For a quick inline stage, `PipelineNodes.Stage<TState>(id, (state, ct) => ...)` does the same
without a class.

## Persisting a stage's result

Mark a stage persistent to record the state it returns in the [ledger](/guides/persistence):

```ts
const recordResult = stage<State>({
  id: "record-result",
  // Record the state returned when this stage succeeds.
  persist: true,
  execute: (state) => ({
    ...state,
    result: {
      source: state.implementation?.source ?? null,
      accepted: state.review?.decision === "Accept",
    },
  }),
});
```
