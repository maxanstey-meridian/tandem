# Collections

A [parallel group](/guides/parallel) has a fixed set of named branches. A collection handles
the other case: a list whose size you only know at run time, such as one model call per
extracted claim, per file or per search result.

A collection selects items from state, runs an operation for each item with bounded
concurrency, and applies the ordered results back to state. Zero items and one item need no
special graph.

::: code-group

```csharp [C#]
var canonicaliser = Agent
    .Create<Claim>("canonicaliser", "Restate the proposition as one canonical claim.", client)
    .WithMessage(claim => claim.Text)
    .WithOutput(new ClaimOutput(), (_, claim) => claim)
    .Build();

var canonicalise = PipelineCollection.Create<SourceState, Claim, Claim>(
    "canonicalise",
    // Select this run's items from state.
    items: state => state.Propositions,
    // The only agents the item operation may call.
    agents: [canonicaliser],
    // One item's operation: here, a single agent call.
    execute: (item, scope, _) => scope.RunAsync(canonicaliser, item),
    // Results arrive in item order, whatever order they finished in.
    apply: (state, claims) => state with { Claims = claims },
    // At most six items in flight at once.
    max: 6);
```

```ts [TypeScript]
const canonicaliser = taskAgent({
  id: "canonicaliser",
  instructions: "Restate the proposition as one canonical claim.",
  client,
  input: Proposition,
  result: Claim,
  message: (proposition) => proposition,
  output: { instructions: "Return the claim's subject and statement.", schema: Claim },
});

const canonicalise = collection({
  id: "canonicalise",
  item: Proposition,
  result: Claim,
  // The only agents the item operation may call.
  agents: [canonicaliser],
  // At most six items in flight at once.
  max: 6,
  // Select this run's items from state.
  items: (state: SourceState) => state.propositions,
  // One item's operation: here, a single agent call.
  execute: (proposition, context) => context.run(canonicaliser, proposition),
  // Results arrive in item order, whatever order they finished in.
  apply: (state, claims) => ({ ...state, claims: [...claims] }),
});
```

:::

Add the collection to a pipeline's nodes and routes like any other participant.

In TypeScript, `taskAgent<Input, Output>` defines a model participant with validated input and
output. In C#, a collection agent is an ordinary agent over the item type.

## The item operation

The item operation is ordinary code, so it can make decisions. It can call one agent, call
another only when the first result needs repair, or skip the model entirely:

```csharp
execute: async (item, scope, _) =>
    item.Text.Contains("repair", StringComparison.Ordinal)
        ? await scope.RunAsync(repairer, item)
        : item
```

It's held to a few rules:

- It can only call agents **declared on that collection**.
- Await each agent call before starting the next. Sibling agent calls in parallel inside one
  item are rejected.
- The scope can't be used after its item completes.
- An operational failure fails the collection, drains active work and skips `apply`.

This is the one place a model visit isn't a graph route of its own. It's allowed because the
collection remains one modelled participant that owns concurrency, cancellation and
observation for its items.

## Observation and persistence

Agent visits inside a collection appear in the source run's own observations and ledger. Agent
definitions can be reused across collections, and Tandem binds each under its collection's
name. Observations and accepted ledger values carry a `visitId`, so concurrent calls can be told
apart without changing their semantic `stepId`.
