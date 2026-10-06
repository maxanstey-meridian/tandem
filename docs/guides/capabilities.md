# Capabilities

A capability is something an agent is explicitly permitted to do, in your application's
language: a typed request, validation, then a typed state transition. Function calling and
tool transport are implementation details underneath.

For Code Writer's implementer, submitting code isn't an arbitrary tool result. It's an
application operation:

::: code-group

```csharp [C#]
// The definition owns the semantic contract.
public sealed class SubmitImplementationCapability
    : IAgentCapabilityDefinition<CodeWriterState, SubmitImplementation>
{
    // This is the function name exposed to the model.
    public string ToolName => "submit_implementation";

    // Tell the model what a complete call must contain.
    public string Instructions => "Submit the complete JavaScript implementation and its rationale.";

    // Reject invalid calls before application code sees them.
    public IValidator<SubmitImplementation> Validator { get; } = new SubmitImplementationValidator();

    // Keep the accepted call readable in observations and the ledger.
    public string Summarize(SubmitImplementation request) =>
        $"Implementation:\n{request.Implementation}\n\nRationale:\n{request.Rationale}";
}

// Bind its accepted request to a typed state transition.
var submitImplementation = AgentCapabilities.Create<CodeWriterState, SubmitImplementation>(
    new SubmitImplementationCapability(),
    (state, submission) => state.RecordImplementation(submission));
```

```ts [TypeScript]
const submitImplementation = capability({
  // This becomes the function name exposed to the implementer.
  name: "submit_implementation",
  // Tell the model what a complete call must contain.
  instructions: "Submit the complete JavaScript implementation and its rationale.",

  // Reject empty source or rationale before application code sees it.
  schema: z.object({
    implementation: z.string().min(1),
    rationale: z.string().min(1),
  }),

  // An accepted call records the new candidate and clears stale checks.
  apply: (state: State, submission) =>
    recordImplementation(state, {
      source: submission.implementation,
      rationale: submission.rationale,
    }),

  // Keep the ledger entry useful without storing the whole prompt.
  summarize: (submission) => submission.rationale,
});
```

:::

C# validators are FluentValidation `IValidator<T>`s. They're authoritative where the generated
JSON Schema can't express a rule.

## Attaching a capability

Attach it to the agent that's allowed to use it:

::: code-group

```csharp [C#]
var implementer = Agent
    .Create<CodeWriterState>(
        // This identity stays stable when the graph loops back.
        "implementer",
        "Implement the requested function.",
        clients.Implementer)
    // Each turn is grounded in the latest application state.
    .WithMessage(ImplementerMessage)
    // Submitting an implementation is the only action it may take.
    .WithCapability(submitImplementation)
    // Keep its conversation when the graph sends work back.
    .ContinueSession()
    .Build();
```

```ts [TypeScript]
const implementer = agent<State>({
  // This identity stays stable when the graph loops back.
  id: "implementer",
  instructions: "Implement the requested function.",
  client: clients.implementer,
  // Each turn is grounded in the latest application state.
  message: implementerMessage,
  // Submitting an implementation is the only action this agent may take.
  capabilities: [submitImplementation],
  // Keep its conversation when verification or review sends work back.
  continueSession: true,
});
```

:::

## What happens on a call

- An invalid call returns a structured tool error to the model before any application code
  runs. The model can correct it in the same session.
- An accepted call applies its state transition and **concludes that agent visit**. The updated
  state goes back to the pipeline, which evaluates the agent's routes.
- Structured output is only parsed when no capability was accepted on that visit.

Tool errors always have one shape:

```json
{ "isError": true, "code": "invalid_tool_call", "message": "...", "problems": [{ "path": "implementation", "message": "..." }] }
```

The codes are `invalid_tool_call`, `conflicting_capability_outcome`,
`capability_acceptance_failed`, `invalid_tool_input` and `action_blocked`.
