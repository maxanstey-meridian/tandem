# Tandem

<p align="center">
  <img
    src="./docs/assets/tui-screenshot.png"
    alt="The Tandem TUI"
    width="1200"
  />
</p>

Tandem is a typed SDK for building agentic applications as explicit pipelines.

Define the facts your application knows, add participants that can act on those facts, and connect them with named
routes. Participants can be model-backed agents, deterministic stages, or typed interactions with the outside world.

Tandem runs in-process on .NET. Use it directly from C#, or author the same
pipeline with the TypeScript SDK. Microsoft Agent Framework owns live workflow execution, model loops,
sessions, and tool dispatch underneath Tandem's typed application model.

## The mental model

A Tandem application is built from a small set of pieces:

<p align="center">
  <img
    src="./docs/assets/tandem-mental-model.svg"
    alt="The Tandem mental model"
    width="1200"
  />
</p>

| Piece           | Think of it as                | What it means                                                     |
|-----------------|-------------------------------|-------------------------------------------------------------------|
| **State**       | The facts                     | Your application's typed lifecycle state: a C# type or Zod schema |
| **Participant** | A box that gets a turn        | The common idea behind agents, stages, and interactions           |
| **Agent**       | A model-backed participant    | Receives instructions and a message derived from current state    |
| **Stage**       | A deterministic participant   | Runs a normal operation and may return updated state              |
| **Capability**  | A typed action                | Something an agent is explicitly permitted to do                  |
| **Interaction** | A typed handoff               | Waits for an external request/response before continuing          |
| **Route**       | An arrow / `if`               | Explicitly decides which participant runs next                    |
| **Output**      | An end point                  | A named successful or failed terminal                             |
| **Outcome**     | Did this participant execute? | Canonical `Success` / `Failed`, separate from domain decisions    |

If you know typed objects, functions, function calling, and `if` statements, you already know most of the ideas Tandem
builds on.

## State is the shared model

Every participant in a pipeline operates over the same application-owned state type.

For our Code Writer example, the important facts are:

### TypeScript

```ts
import { z } from "zod";

export const State = z.object({
    requirements: z.array(z.string().min(1)).min(1),
    implementation: ImplementationCandidate.nullable(),
    verification: VerificationResult.nullable(),
    review: ReviewDecision.nullable(),
});

export type State = z.infer<typeof State>;
```

### C#

```csharp
public sealed record CodeWriterState(
    IReadOnlyList<string> Requirements,
    ImplementationCandidate? Implementation = null,
    VerificationResult? Verification = null,
    ReviewDecision? Review = null
);
```

State contains **application facts**. It does not need to contain Tandem bookkeeping such as the current node, run ID,
invocation ID, route name, or resume position.

When something meaningful changes return a new state representing the new facts, and the graph decides where those facts
send the run next.

## Agents

An agent has:

* a stable identity;
* instructions;
* a model client;
* a message derived from current state;
* optional typed capabilities;
* optional structured output; and
* optional session continuation.

### TypeScript

```ts
const reviewer = agent<State, ReviewDecision>({
    id: "reviewer",
    instructions:
        "Review the exact implementation against the requirements and passing verification evidence.",
    client: clients.reviewer,

    message: (state) =>
        [
            `Requirements: ${JSON.stringify(state.requirements)}`,
            `Exact source: ${state.implementation!.source}`,
            `Passing verification evidence: ${JSON.stringify(state.verification)}`,
        ].join("\n"),

    output: {
        instructions:
            "Return Accept or RequestChanges with a concise summary and concrete findings.",
        schema: ReviewDecision,
        apply: recordReview,
    },
});
```

### C#

```csharp
var reviewer = Agent
    .Create<CodeWriterState>(
        "reviewer",
        "Review the exact implementation against the requirements and passing verification evidence.",
        clients.Reviewer)
    .WithMessage(state =>
        $"Exact source: {state.Implementation!.Source}\n"
        + $"Passing verification evidence: {JsonSerializer.Serialize(state.Verification)}")
    .WithOutput(
        new ReviewDecisionOutput(),
        (state, review) => state.RecordReview(review))
    .Build();
```

`continueSession: true` in TypeScript or `.ContinueSession()` in C# sets whether the agent session should be reused
when the pipeline routes back to that agent

## Typed model output becomes application state

Tandem's structured outputs and capabilities give that boundary an application-owned type. For
example, Code Writer does not ask the Reviewer for arbitrary prose and then ask another model what that prose means.

### TypeScript

```ts
export const ReviewDecision = z
    .object({
        decision: z.enum(["Accept", "RequestChanges"]),
        summary: z.string().min(1),
        findings: z.array(z.string().min(1)),
    })
    .refine(
        (review) =>
            review.decision !== "RequestChanges" ||
            review.findings.length > 0,
        {
            path: ["findings"],
            message: "RequestChanges requires at least one finding",
        },
    );
```

### C#

```csharp
public enum ReviewDisposition
{
    Accept,
    RequestChanges,
}

public sealed record ReviewDecision(
    ReviewDisposition Decision,
    string Summary,
    IReadOnlyList<string> Findings
);
```

The output is validated before Tandem applies it to state.

After that, this:

```ts
state.review?.decision === "Accept"
```

or this:

```csharp
state.Review?.Decision == ReviewDisposition.Accept
```

is just an ordinary application fact and a route can make an ordinary deterministic decision from it.

## Capabilities

For the Implementer, submitting code is not an arbitrary tool result. It is an application operation with a request
type, validation, a summary, and a state transition.

### TypeScript

```ts
const submitImplementation = capability({
    name: "submit_implementation",
    instructions:
        "Submit the complete JavaScript implementation and its rationale.",

    schema: z.object({
        implementation: z.string().min(1),
        rationale: z.string().min(1),
    }),

    apply: (state: State, submission) =>
        recordImplementation(state, {
            source: submission.implementation,
            rationale: submission.rationale,
        }),

    summarize: (submission) => submission.rationale,
});
```

Attach it to the intended agent:

```ts
const implementer = agent<State>({
    id: "implementer",
    instructions: "Implement the requested function.",
    client: clients.implementer,
    message: implementerMessage,
    capabilities: [submitImplementation],
    continueSession: true,
});
```

### C#

In C#, the capability definition owns its semantic contract:

```csharp
public sealed class SubmitImplementationCapability
    : IAgentCapabilityDefinition<CodeWriterState, SubmitImplementation>
{
    public string ToolName => "submit_implementation";

    public string Instructions =>
        "Submit the complete JavaScript implementation and its rationale.";

    public IValidator<SubmitImplementation> Validator { get; } =
        new SubmitImplementationValidator();

    public string Summarize(SubmitImplementation request) =>
        request.Rationale;
}
```

Then bind its accepted request to a typed state transition:

```csharp
var submitImplementation =
    AgentCapabilities.Create<CodeWriterState, SubmitImplementation>(
        new SubmitImplementationCapability(),
        (state, submission) =>
            state.RecordImplementation(submission));
```

And attach it to the agent:

```csharp
var implementer = Agent
    .Create<CodeWriterState>(
        "implementer",
        "Implement the requested function.",
        clients.Implementer)
    .WithMessage(ImplementerMessage)
    .WithCapability(submitImplementation)
    .ContinueSession()
    .Build();
```

An accepted capability call concludes that agent visit. The updated state is then handed back to the pipeline, which
evaluates the configured routes.

## Stages

Stages use the same state as agents and are routed in exactly the same graph.

Code Writer's verification step is a stage because testing the submitted implementation does not require another model.

### TypeScript

```ts
const verification = stage<State>({
    id: "verification",

    execute: async (state) =>
        recordVerification(
            state,
            // Some deterministic/non-LLM implementation assessment.
            await assessImplementation(state.implementation!.source),
        ),
});
```

### C#

```csharp
[PipelineStage("verification")]
public sealed partial class VerificationStage
{
    private readonly ImplementationAssessment _assessment = new();

    public async ValueTask<CodeWriterState> ExecuteAsync(
        CodeWriterState state,
        CancellationToken cancellationToken)
    {
        var source =
            state.Implementation?.Source
            ?? throw new InvalidOperationException(
                "Verification requires an implementation.");

        var verification =
            await _assessment.AssessAsync(source, cancellationToken);

        return state.RecordVerification(verification);
    }
}
```

A stage can perform whatever operation belongs at that point in the lifecycle: validation, calculation, database work,
an API call, compilation, verification, transformation, or another deterministic application operation. It does not need
to know who runs before or after it.

## Interactions

The pipeline reaches the interaction, creates a typed request from current state, waits for a typed response, applies
that response to state, and continues through its routes.

The host decides what actually answers the request: a web UI, CLI, operator, another application, or another external
channel.

### TypeScript

```ts
const customerReply = interaction<
    SupportState,
    CustomerQuestion,
    CustomerReply
>({
    id: "customer-reply",
    requestSchema: CustomerQuestion,
    responseSchema: CustomerReply,

    request: (state) => state.createCustomerQuestion(),

    apply: (state, reply) =>
        state.recordCustomerReply(reply),
});
```

A host supplies the handler separately:

```ts
const handlers = interactions().handle(
    customerReply,
    async (question) => askCustomer(question),
);

const result = await run(support, initialState, {
    interactions: handlers,
});
```

### C#

```csharp
var customerReply =
    PipelineNodes.WaitFor<
        SupportState,
        CustomerQuestion,
        CustomerReply>(
        "customer-reply",
        state => state.CreateCustomerQuestion(),
        (state, reply) => state.RecordCustomerReply(reply));
```

Interactions are live and process-owned; they are not a durable workflow scheduler.

## Routes are the control flow

Participants do not choose their successors.

A route has:

* a source;
* a destination;
* a semantic label;
* optionally a standard execution outcome; and
* optionally a predicate over typed state.

Domain decisions belong in state.

`Success` and `Failed` mean whether a participant executed successfully; they are not a catalogue of domain outcomes
such as `Approved`, `Rejected`, `ChangesRequested`, or `Escalated`.

### TypeScript

```ts
return pipeline({
    name: "code-writer",
    state: State,

    nodes: [
        implementer,
        verification,
        reviewer,
        done,
        failed,
    ],

    start: implementer,

    routes: [
        route({
            from: implementer,
            to: verification,
            outcome: "success",
            label: "implementation submitted",
        }),

        route({
            from: implementer,
            to: failed,
            outcome: "failed",
            label: "implementer failed",
        }),

        route({
            from: verification,
            to: reviewer,
            when: (state) =>
                state.verification?.passed === true,
            label: "verification passed",
        }),

        route({
            from: verification,
            to: implementer,
            when: (state) =>
                state.verification?.passed === false,
            label: "verification failed",
        }),

        route({
            from: reviewer,
            to: implementer,
            outcome: "success",
            when: (state) =>
                state.review?.decision === "RequestChanges",
            label: "changes requested",
        }),

        route({
            from: reviewer,
            to: done,
            outcome: "success",
            when: (state) =>
                state.review?.decision === "Accept",
            label: "accepted",
        }),

        route({
            from: reviewer,
            to: failed,
            outcome: "failed",
            label: "reviewer failed",
        }),
    ],

    outputs: [done, failed],
    persist: true,
});
```

### C#

```csharp
public Pipeline<CodeWriterState> Build() =>
    Pipeline
        .Start(
            at: codeWriter.Implementer,
            name: "code-writer",
            description:
                "Implement and verify a function until review accepts it."
        )
        .Route(
            on: codeWriter.Implementer.Success,
            to: codeWriter.Verification,
            label: "implementation submitted"
        )
        .Route(
            on: codeWriter.Implementer.Failed,
            to: codeWriter.Failed,
            label: "implementer failed"
        )
        .Route(
            from: codeWriter.Verification,
            when: state =>
                state.Verification?.Passed is true,
            to: codeWriter.Reviewer,
            label: "verification passed"
        )
        .Route(
            from: codeWriter.Verification,
            when: state =>
                state.Verification?.Passed is false,
            to: codeWriter.Implementer,
            label: "verification failed"
        )
        .Route(
            on: codeWriter.Reviewer.Success,
            when: state =>
                state.Review?.Decision
                    == ReviewDisposition.RequestChanges,
            to: codeWriter.Implementer,
            label: "changes requested"
        )
        .Route(
            on: codeWriter.Reviewer.Success,
            when: state =>
                state.Review?.Decision
                    == ReviewDisposition.Accept,
            to: codeWriter.Complete,
            label: "accepted"
        )
        .Route(
            on: codeWriter.Reviewer.Failed,
            to: codeWriter.Failed,
            label: "reviewer failed"
        )
        .Persist()
        .Build(
            codeWriter.Complete,
            codeWriter.Failed
        );
```

The two authoring surfaces describe the same machine.

## How a run executes

At runtime, the model is simple:

1. Start with the caller's initial typed state.
2. Run the current participant.
3. Accept its validated result or state transition.
4. Evaluate that participant's outgoing routes in order.
5. Follow the first matching route.
6. Run the next participant.
7. Repeat until the run completes, fails, is cancelled, or waits at an interaction.

There is no second application-level orchestration model behind the graph.

The configured pipeline is the lifecycle.

## Outputs

A successful output and a failed output are distinct, inspectable destinations rather than implicit conventions.

### TypeScript

```ts
const done = output<State>({
    id: "done",
    summary: (state) => state.review!.summary,
});

const failed = output<State>({
    id: "failed",
    failed: true,
    summary: () =>
        "An agent failed before the code could be accepted.",
});
```

### C#

```csharp
var complete =
    PipelineNodes.Complete(new CodeWriterComplete());

var failed =
    PipelineNodes.Failed(new CodeWriterFailed());
```

A pipeline explicitly declares the outputs through which a run may finish.

## Persistence

Enable persistence with `persist: true` in TypeScript or `.Persist()` in C#.

Persistent pipelines can record:

* accepted structured agent outputs;
* accepted capability calls;
* interaction requests and answers;
* declared failures; and
* state returned by persistent stages.

Persistence is attached to the semantic boundary where a value becomes accepted.

For example:

```ts
const recordResult = stage<State>({
    id: "record-result",
    persist: true,

    execute: (state) => ({
        ...state,
        result: {
            source: state.implementation?.source ?? null,
            accepted:
                state.review?.decision === "Accept",
        },
    }),
});
```

Once the stage succeeds, the state it returned is available in the ledger.

### Inspecting accepted values

Runs created by `Tandem.Tool` use the Tool ledger, stored at:

```text
$TANDEM_HOME/ledger.sqlite3
```

when `TANDEM_HOME` is configured.

Inspect a run by ID:

```sh
# Complete run timeline.
dotnet run --project src/Tandem.Tool -- inspect <run-id>

# Only values accepted into the run.
dotnet run --project src/Tandem.Tool -- inspect <run-id> --accepted

# Filter accepted values.
dotnet run --project src/Tandem.Tool -- inspect <run-id> --accepted --step reviewer

# Emit JSON for another tool.
dotnet run --project src/Tandem.Tool -- inspect <run-id> --accepted --json
```

Applications using another ledger path can read accepted values through `inspectAccepted` in TypeScript or
`SqliteLedgerStore` in C#.

## Running a pipeline

Your application owns the process and starts a pipeline with its initial state.

### TypeScript

```ts
import { run } from "@tandem/sdk";

const result = await run(
    codeWriter,
    initialState,
    {
        signal: AbortSignal.timeout(180_000),
        ledgerPath: "code-writer.sqlite3",
    },
);

console.log(result.succeeded);
console.log(result.state);
```

### C#

```csharp
var result =
    await new PipelineRunner().RunAsync(
        codeWriter,
        initialState,
        cancellationToken: cancellationToken);

Console.WriteLine(result.Status);
Console.WriteLine(result.State);
```

Persistent C# pipelines receive their persistence observer through `PipelineRunOptions`.
The [Code Writer host](examples/code-writer/csharp/Program.cs) shows the complete setup and run terminalisation.

## C# and TypeScript

Tandem has one execution model with two authoring surfaces.

|                       | C#                                            | TypeScript                            |
|-----------------------|-----------------------------------------------|---------------------------------------|
| **State**             | Normal typed application state                | Zod schema + inferred TypeScript type |
| **Stages**            | Generated from `[PipelineStage]` classes      | `stage(...)`                          |
| **Agents**            | `Agent.Create<TState>(...)`                   | `agent<TState>(...)`                  |
| **Capabilities**      | Typed definitions + validators                | Zod request schemas                   |
| **Structured output** | Typed output definitions + validators         | Zod output schemas                    |
| **Interactions**      | `PipelineNodes.WaitFor<...>`                  | `interaction(...)`                    |
| **Routes**            | Fluent `Pipeline.Route(...)`                  | `route(...)`                          |
| **Runtime**           | Tandem + Microsoft Agent Framework in-process | The same Tandem/.NET engine           |


TypeScript pplications import `@tandem/sdk`; they do not build or manually load .NET assemblies.

See [`typescript/README.md`](typescript/README.md) for TypeScript-specific runtime and packaging details.

## Microsoft Agent Framework

Tandem owns the application-facing model:

* typed state;
* participants;
* agent definitions;
* structured outputs;
* capabilities;
* interactions;
* semantic routes;
* terminals; and
* persistence of accepted values.

Microsoft Agent Framework owns the lower-level live execution mechanics:

* workflow execution;
* model loops;
* sessions;
* tool dispatch; and
* workflow events.

Those mechanics stay below Tandem's ordinary authoring surface.

For features that deliberately need to participate in execution mechanics, Tandem keeps a separate Advanced surface
rather than requiring ordinary pipelines to understand runtime envelopes, executor bindings, provider transport, or
framework node identities.

## Examples

The repository contains matching C# and TypeScript examples for:

* **Songwriter** — a small agent pipeline with branching and revision;
* **Debate** — multiple agents, capabilities, and session continuation; and
* **Code Writer** — implementation, deterministic verification, typed review, loops, and persistence.

See [`examples`](examples).

### Run the examples

The current examples use DS4 through OpenRouter to create work and a local `gpt-5.6-sol` endpoint to review it.

They require an `OPENROUTER_API_KEY` and a running [`openai-oauth`](https://github.com/EvanZhouDev/openai-oauth) proxy.

Start and authenticate the local Sol endpoint:

```sh
npx --yes openai-oauth@latest
```

Run a TypeScript example from the repository root:

```sh
# Install once.
pnpm --dir typescript install --frozen-lockfile

OPENROUTER_API_KEY=... pnpm --dir typescript run:code-writer

OPENROUTER_API_KEY=... \
  pnpm --dir typescript run:debate -- \
  "Should cities remove downtown parking?"

OPENROUTER_API_KEY=... \
  pnpm --dir typescript run:songwriter -- \
  "A hopeful song about coming home"
```

Or run the matching C# examples:

```sh
OPENROUTER_API_KEY=... \
  dotnet run --project examples/code-writer/csharp

OPENROUTER_API_KEY=... \
  dotnet run --project examples/debate/csharp -- \
  "Should cities remove downtown parking?"

OPENROUTER_API_KEY=... \
  dotnet run --project examples/songwriter/csharp -- \
  "A hopeful song about coming home"
```

Code Writer also requires Node.js for its JavaScript verifier.

## Documentation

For the complete authoring model, see:

* [`docs/pipeline-authoring.md`](docs/pipeline-authoring.md) — pipeline state, stages, agents, routing, interactions,
  persistence, and Advanced authoring;
* [`docs/agent-authoring-decision.md`](docs/agent-authoring-decision.md) — the design and invariants behind agent
  definitions and capabilities;
* [`typescript/README.md`](typescript/README.md) — TypeScript SDK requirements, packages, chat clients, persistence, and
  development; and
* [`CONTRIBUTING.md`](CONTRIBUTING.md) — architecture boundaries and invariants for contributors.

## License

[MIT](LICENSE)
