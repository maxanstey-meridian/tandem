# Tandem

Tandem is a typed SDK for building agentic applications as explicit pipelines.
Agents, ordinary code, and human interactions share application state; named routes
decide what happens next.

The pipeline is the lifecycle. There is no hidden coordinator deciding which agent
runs next, and no application-level agent loop. Microsoft Agent Framework owns live
execution, sessions, model loops, and tool dispatch underneath.

Tandem runs in-process on .NET and builds on Microsoft Agent Framework. Use it
directly from C#, or author the same pipeline from a Node application with the
TypeScript SDK. The TypeScript API crosses into the same engine, so routing, model
execution, capabilities, validation, and persistence behave the same in either
language.

## TypeScript

This abbreviated code-writer asks one agent to implement a function, verifies it
with ordinary TypeScript, and asks another agent to review the exact result. Failed
verification or requested changes route back to the implementer.

```ts
import { agent, capability, output, pipeline, route, stage } from "@tandem/sdk";
import { z } from "zod";

const State = z.object({
  // This is the job we want completed.
  requirements: z.array(z.string()),
  // The implementer fills this in when it submits working code.
  implementation: z.string().nullable(),
  // Ordinary TypeScript verification records whether that code works.
  verified: z.boolean(),
  // The reviewer makes the final decision, or sends the work around again.
  review: z.enum(["Accept", "RequestChanges"]).nullable(),
  // We will use this later to show how a stage can save a useful result.
  result: z
    .object({ source: z.string().nullable(), accepted: z.boolean() })
    .nullable(),
});
// TypeScript now knows the exact state shape without a second declaration.
type State = z.infer<typeof State>;

const submitImplementation = capability({
  // This becomes the function the model can call.
  name: "submit_implementation",
  // The model sees what the function is for.
  instructions: "Submit the complete implementation.",
  // Tandem rejects a call that does not contain actual source code.
  schema: z.object({ source: z.string().min(1) }),
  // Only an accepted call is allowed to change our state.
  apply: (state: State, submission) => ({
    ...state,
    implementation: submission.source,
    // New code makes the previous verification and review stale.
    verified: false,
    review: null,
  }),
  // This is the human-readable account of what happened.
  summarize: () => "Implementation submitted",
});

const implementer = agent<State>({
  // This name is how the agent appears in the pipeline and ledger.
  id: "implementer",
  // These are the standing instructions for every visit.
  instructions: "Implement the requested function.",
  // Bring any Microsoft.Extensions.AI-compatible chat client.
  client: clients.implementer,
  // Each visit receives the latest implementation, checks, and review.
  message: (state) => JSON.stringify(state),
  // Submitting code is the only action this agent may take.
  capabilities: [submitImplementation],
  // Keep the conversation when a failed check sends the work back.
  continueSession: true,
});

const verification = stage<State>({
  // This runs normal TypeScript rather than asking another agent.
  id: "verification",
  execute: async (state, { signal }) => ({
    ...state,
    // Run the code and put the result back into shared state.
    verified: await verify(state.implementation!, signal),
  }),
});

const reviewer = agent<State, { decision: "Accept" | "RequestChanges" }>({
  // Give the second agent its own place in the graph and ledger.
  id: "reviewer",
  // Its only job is to judge code that has already passed verification.
  instructions: "Review the verified implementation.",
  // The reviewer can use a different model from the implementer.
  client: clients.reviewer,
  // The reviewer sees the exact source that passed verification.
  message: (state) => state.implementation!,
  output: {
    // Ask for one small, explicit decision rather than free-form prose.
    instructions: "Return Accept or RequestChanges.",
    // Anything else is corrected before it can reach the application.
    schema: z.object({ decision: z.enum(["Accept", "RequestChanges"]) }),
    // Once accepted, the decision becomes an ordinary fact in state.
    apply: (state, result) => ({ ...state, review: result.decision }),
  },
});

// Successful runs end here.
const done = output<State>({
  // Routes refer to the terminal by this stable name.
  id: "done",
  // The caller can show this summary directly.
  summary: () => "Implementation accepted",
});

// Agent errors end somewhere different.
const failed = output<State>({
  id: "failed",
  // Mark this terminal as an unsuccessful result.
  failed: true,
  // Give the caller a useful failure message.
  summary: () => "Code writer failed",
});

export const codeWriter = pipeline({
  // Give this lifecycle a stable name.
  name: "code-writer",
  // Every node reads and returns this same state shape.
  state: State,
  // List everything that can run.
  nodes: [implementer, verification, reviewer, done, failed],
  // The implementer gets the first turn.
  start: implementer,
  // Routes are checked in this order after each node finishes.
  routes: [
    // Submitted code always goes through the same checks.
    route({
      from: implementer,
      outcome: "success",
      to: verification,
      label: "submitted",
    }),

    // If the implementer itself fails, there is no candidate to verify.
    route({
      from: implementer,
      outcome: "failed",
      to: failed,
      label: "implementer failed",
    }),

    // Code that passes its tests is ready for model review.
    route({
      from: verification,
      when: (state) => state.verified,
      to: reviewer,
      label: "verified",
    }),

    // Failed tests send the latest state back around as concrete feedback.
    route({
      from: verification,
      when: (state) => !state.verified,
      to: implementer,
      label: "failed",
    }),

    // An accepted review finishes the run.
    route({
      from: reviewer,
      outcome: "success",
      when: (state) => state.review === "Accept",
      to: done,
      label: "accepted",
    }),

    // Requested changes return to the same implementer conversation.
    route({
      from: reviewer,
      outcome: "success",
      when: (state) => state.review === "RequestChanges",
      to: implementer,
      label: "changes requested",
    }),

    // A reviewer fault is different from a valid RequestChanges decision.
    route({
      from: reviewer,
      outcome: "failed",
      to: failed,
      label: "reviewer failed",
    }),
  ],
  // These are the only places from which the run may finish.
  outputs: [done, failed],
});
```

TypeScript is authoring sugar over Tandem. It validates the same contracts and
registers the same nodes and routes; the pipeline still runs in C# through Tandem
and Microsoft Agent Framework.

## C#

The same abbreviated pipeline in C# uses the same concepts directly:

```csharp
public sealed record CodeWriterState(
    // The request both agents are working towards.
    IReadOnlyList<string> Requirements,
    // Source accepted from the implementer's capability call.
    string? Implementation = null,
    // The result produced by ordinary C# verification.
    bool Verified = false,
    // The reviewer's accepted decision, used by the outgoing routes.
    ReviewDisposition? Review = null);

var submitImplementation = AgentCapabilities.Create<CodeWriterState, SubmitImplementation>(
    // This supplies the function name, instructions, schema, and validation.
    new SubmitImplementationCapability(),
    // Tandem applies this only after the model call has been accepted.
    (state, submission) => state with
    {
        Implementation = submission.Implementation,
        // New code invalidates any verdict about the previous version.
        Verified = false,
        Review = null,
    });

var implementer = Agent
    .Create<CodeWriterState>(
        // The agent keeps the same identity wherever the graph sends it.
        "implementer",
        // These instructions stay with it across the whole run.
        "Implement the requested function.",
        // The model client is supplied by the host, not hidden by Tandem.
        clients.Implementer)
    // Every visit includes the latest state.
    .WithMessage(state => JsonSerializer.Serialize(state))
    // This is the one action the implementer may take.
    .WithCapability(submitImplementation)
    // Preserve the conversation when verification sends it back around.
    .ContinueSession()
    .Build();

// The implementation then passes through normal C# tests, not another prompt.
var verification = new VerificationStage();

var reviewer = Agent
    .Create<CodeWriterState>(
        "reviewer",
        "Review the verified implementation.",
        clients.Reviewer)
    // Review the exact source that already passed verification.
    .WithMessage(state => state.Implementation!)
    .WithOutput(
        // This definition owns the response shape and its validation.
        new ReviewDecisionOutput(),
        // Only an accepted decision is allowed to update state.
        (state, result) => state with { Review = result.Decision })
    .Build();

// These two nodes make success and failure clear to the caller.
var done = PipelineNodes.Complete(new CodeWriterComplete());
var failed = PipelineNodes.Failed(new CodeWriterFailed());

var codeWriter = Pipeline
    // Begin with the implementer and give the lifecycle a name.
    .Start(implementer, "code-writer")
    // Submitted work must pass through verification.
    .Route(
        on: implementer.Success,
        to: verification,
        label: "submitted")
    // An implementer fault has no candidate to send onwards.
    .Route(
        on: implementer.Failed,
        to: failed,
        label: "implementer failed")
    // Passing verification moves the exact candidate to review.
    .Route(
        from: verification,
        when: state => state.Verified,
        to: reviewer,
        label: "verified")
    // Failed verification sends its updated state back to the implementer.
    .Route(
        from: verification,
        when: state => !state.Verified,
        to: implementer,
        label: "failed")
    // Accept finishes the run.
    .Route(
        on: reviewer.Success,
        when: state => state.Review == ReviewDisposition.Accept,
        to: done,
        label: "accepted")
    // RequestChanges is another valid result, so it loops rather than fails.
    .Route(
        on: reviewer.Success,
        when: state => state.Review == ReviewDisposition.RequestChanges,
        to: implementer,
        label: "changes requested")
    // A reviewer fault is kept separate from its typed review decision.
    .Route(
        on: reviewer.Failed,
        to: failed,
        label: "reviewer failed")
    // A run can leave the graph only through these named outcomes.
    .Build(done, failed);
```

## Persistence

Add `persist: true` in TypeScript or `.Persist()` in C# and Tandem records what was
accepted during the run:

- structured agent outputs;
- accepted capability calls;
- human interaction requests and answers;
- declared failures; and
- state returned by ordinary stages.

A stage is just a typed pipe over state. If you want a transformed shape in the
ledger, return that shape from a persistent stage:

```ts
const recordResult = stage<State>({
  // This name is also the key used to find the value later.
  id: "record-result",
  // Retain the value accepted when this stage succeeds.
  persist: true,
  execute: (state) => ({
    // Keep the existing state.
    ...state,
    // Shape the useful result here; Tandem records exactly what we return.
    result: {
      source: state.implementation,
      accepted: state.review === "Accept",
    },
  }),
});
```

Once `record-result` succeeds, its returned state is in the ledger. There is no save
callback to maintain and Tandem does not take automatic state snapshots. It records
the value when a persistent stage, agent, capability, or interaction succeeds. It
does not store prompts, reasoning, or streaming text, and it does not make live MAF
runs resumable.

## Running Pipelines

Your application owns the process and starts a pipeline with its initial state.

In TypeScript:

```ts
import { run } from "@tandem/sdk";

const result = await run(codeWriter, initialState, {
  signal: AbortSignal.timeout(180_000),
  // Supply a path only when the pipeline persists values.
  ledgerPath: "code-writer.sqlite3",
});

console.log(result.succeeded, result.state);
```

In C#:

```csharp
var result = await new PipelineRunner().RunAsync(
    codeWriter,
    initialState,
    cancellationToken: cancellationToken);

Console.WriteLine(result.Status);
Console.WriteLine(result.State);
```

Persistent C# pipelines also receive a `SqlitePipelineObserver` through
`PipelineRunOptions`; the [Code Writer host](examples/code-writer/csharp/Program.cs)
shows the complete setup and run terminalization.

### Run The Examples

The examples use DS4 through OpenRouter to create work and a local `gpt-5.6-sol`
endpoint to review it. They require an `OPENROUTER_API_KEY` and a running
[`openai-oauth`](https://github.com/EvanZhouDev/openai-oauth) proxy. Code Writer also
requires Node.js for its JavaScript verifier.

Start and authenticate the local Sol endpoint:

```sh
npx --yes openai-oauth@latest
```

Run any TypeScript example from the repository root:

```sh
# Install once.
pnpm --dir typescript install --frozen-lockfile

OPENROUTER_API_KEY=... pnpm --dir typescript run:code-writer
OPENROUTER_API_KEY=... pnpm --dir typescript run:debate -- "Should cities remove downtown parking?"
OPENROUTER_API_KEY=... pnpm --dir typescript run:songwriter -- "A hopeful song about coming home"
```

Or run the same examples in C# with .NET 10:

```sh
OPENROUTER_API_KEY=... dotnet run --project examples/code-writer/csharp
OPENROUTER_API_KEY=... dotnet run --project examples/debate/csharp -- "Should cities remove downtown parking?"
OPENROUTER_API_KEY=... dotnet run --project examples/songwriter/csharp -- "A hopeful song about coming home"
```

Complete C# and TypeScript source for Code Writer, Debate, and Songwriter lives in
[`examples`](examples).
