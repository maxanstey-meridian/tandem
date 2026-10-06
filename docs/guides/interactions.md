# Interactions

An interaction is a typed handoff to the outside world. The pipeline reaches it, builds a typed
request from current state, waits for a typed response, applies that response to state, and
carries on through its routes.

The host decides what actually answers: a web UI, a CLI, an operator, another application or
another external channel. The pipeline only knows the types.

::: code-group

```csharp [C#]
var customerReply = PipelineNodes.WaitFor<SupportState, CustomerQuestion, CustomerReply>(
    // The graph pauses at this named handoff.
    "customer-reply",
    // Build the question from the latest support facts.
    state => state.CreateCustomerQuestion(),
    // Turn the accepted reply back into application state.
    (state, reply) => state.RecordCustomerReply(reply));
```

```ts [TypeScript]
const customerReply = interaction<SupportState, CustomerQuestion, CustomerReply>({
  // The graph pauses at this named handoff.
  id: "customer-reply",
  // Validate both what leaves the pipeline and what comes back.
  requestSchema: CustomerQuestion,
  responseSchema: CustomerReply,
  // Build the question from the latest support facts.
  request: (state) => state.createCustomerQuestion(),
  // Turn the accepted reply back into application state.
  apply: (state, reply) => state.recordCustomerReply(reply),
});
```

:::

## Answering it

The host binds a handler for each interaction when it starts a run:

::: code-group

```csharp [C#]
var handlers = new PipelineInteractionHandlers().Handle(
    customerReply,
    // The host decides how this request reaches the customer.
    (context, ct) => AskCustomerAsync(context.Request, ct));

var result = await new PipelineRunner().RunAsync(
    support,
    initialState,
    // Bind this live channel for this run only.
    new PipelineRunOptions(Interactions: handlers),
    cancellationToken);
```

```ts [TypeScript]
const handlers = interactions().handle(
  customerReply,
  // The host decides how this request reaches the customer.
  async (question) => askCustomer(question),
);

const result = await run(support, initialState, {
  // Bind this live channel for this run only.
  interactions: handlers,
});
```

:::

In C#, the handler also receives the run ID, the request ID and the interaction ID on its
context, so it can correlate the request with an external channel.

When the pipeline is [persistent](/guides/persistence), each request and its answer are
recorded in the ledger.

## Live, not durable

Interactions are live and owned by the process. The run waits in memory for the answer. If the
process stops, the run stops with it, and the ledger can't reopen it. For a wait measured in
days, end the run at an output and start a new run when the answer arrives.
