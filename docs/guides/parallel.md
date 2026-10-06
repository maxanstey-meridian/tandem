# Parallel Groups

When several operations depend on the same facts but not on each other, a parallel group runs
them together and explicitly combines what each learned.

- Every named branch gets isolated state.
- All branches run concurrently, and all must succeed before merge runs.
- Merge receives the original baseline and each branch's resulting state. Your code decides how
  the facts combine, so completion order never does.

::: code-group

```csharp [C#]
var classify = PipelineNodes.Parallel(
    // The parent graph routes through this one semantic participant.
    id: "classify-framing",

    // Give every branch its own application-state graph.
    clone: state => state with { Findings = [.. state.Findings] },

    // Branch names describe their role inside this group.
    branches:
    [
        PipelineBranch.Create("world", worldClassifier),
        PipelineBranch.Create("epistemic", epistemicClassifier),
        PipelineBranch.Create("temporal", temporalClassifier),
    ],

    // Merge by authored branch name, never by completion order.
    merge: results =>
        results.Baseline with
        {
            World = results.State("world").World,
            Epistemic = results.State("epistemic").Epistemic,
            Temporal = results.State("temporal").Temporal,
        });

var pipeline = Pipeline
    .Start(classify, "classify-framing")
    // Continue only after every branch succeeds and merge completes.
    .Route(classify.Success, done, "classification complete")
    // A branch failure follows the normal failed outcome.
    .Route(classify.Failed, failed, "classification failed")
    .Build(done, failed);
```

```ts [TypeScript]
const classify = parallel({
  // The parent graph routes through this one semantic participant.
  id: "classify-framing",

  // Each named participant receives an isolated copy of the same facts.
  branches: {
    world: worldClassifier,
    epistemic: epistemicClassifier,
    temporal: temporalClassifier,
  },

  // Combine application facts explicitly; completion order changes nothing.
  merge: (baseline, results) => ({
    ...baseline,
    world: results.world.world,
    epistemic: results.epistemic.epistemic,
    temporal: results.temporal.temporal,
  }),
});

const framing = pipeline({
  name: "classify-framing",
  state: FramingState,
  // Branch participants belong to classify, so only the group is listed here.
  nodes: [classify, done, failed],
  start: classify,
  routes: [
    // Every branch succeeded and the merged state is ready.
    route({ from: classify, outcome: "success", to: done, label: "classification complete" }),
    // A branch failure skips merge.
    route({ from: classify, outcome: "failed", to: failed, label: "classification failed" }),
  ],
  outputs: [done, failed],
});
```

:::

## Limiting concurrency

Set `max: 5` to run at most five branches at once. Each completed branch frees its slot
straight away, so work isn't split into fixed waves. Without `max`, every branch runs
concurrently.

The limit applies to each invocation of the group, not to a provider or the whole process.
Queued branches honour cancellation. `max` must be a positive 32-bit integer.

## Rules

- Branches may be agents or stages. Outputs, interactions, nested parallel groups and branch
  subgraphs aren't supported as branches.
- A branch participant belongs to its group and can't also appear elsewhere in the parent
  graph.
- Branch observations can arrive in any order.
- External side effects aren't rolled back if a sibling fails.
- Caller cancellation reaches every active branch.

C# `clone` must isolate every mutable object branch code can reach. TypeScript gets that
isolation for free, because state crosses a validated JSON boundary.

When persistence is on, each branch's result is recorded under its branch participant's ID,
and the merged state under the group's ID.
