# Tandem

Tandem is a typed SDK for building agentic applications as explicit pipelines. Define the lifecycle in code, then run
and inspect it.

Looking for a ready-to-run coding pipeline? [Cadence](https://github.com/maxanstey-meridian/cadence) uses Tandem to
plan, implement, verify, and review changes in an isolated workspace.

```typescript
const myPipeline = pipeline({
  // Give the complete lifecycle one name in logs and the ledger.
  name: "coder-pipeline",
  // Every node reads and returns this same state shape.
  state: State,

  // List everything that can take a turn or finish the run.
  nodes: [implementer, reviewer, done, failed],
  // The implementer receives the initial state first.
  start: implementer,

  routes: [
    // Submitted code goes straight to review.
    route({
      from: implementer,
      to: reviewer,
      outcome: "success",
    }),
    // Accepted work finishes the run.
    route({
      from: reviewer,
      to: done,
      outcome: "success",
      when: (state) => state.review?.decision === "Accept",
    }),
    // Requested changes go back around with the same state.
    route({
      from: reviewer,
      to: implementer,
      outcome: "success",
      when: (state) => state.review?.decision === "RequestChanges",
    }),
    // An implementer fault has no work to review.
    route({
      from: implementer,
      to: failed,
      outcome: "failed",
    }),
    // A reviewer fault is different from requesting changes.
    route({
      from: reviewer,
      to: failed,
      outcome: "failed",
    }),
  ],

  // These are the only places the run may finish.
  outputs: [done, failed],
  // Keep accepted values so this run can be inspected later.
  persist: true,
});
```

<p align="center">
  <img
    src="./docs/assets/tui-screenshot.png"
    alt="The Tandem TUI"
    width="1200"
  />
</p>

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
| **Parallel group** | Independent work joined together | Runs named agents or stages from isolated state, then explicitly merges their results |
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
    // The job both agents are working towards.
    requirements: z.array(z.string().min(1)).min(1),
    // Source and rationale accepted from the implementer.
    implementation: ImplementationCandidate.nullable(),
    // Evidence produced by running normal verification code.
    verification: VerificationResult.nullable(),
    // The reviewer's accepted decision.
    review: ReviewDecision.nullable(),
});

// The schema is also the single source of the TypeScript type.
export type State = z.infer<typeof State>;
```

### C#

```csharp
public sealed record CodeWriterState(
    // The job both agents are working towards.
    IReadOnlyList<string> Requirements,
    // Source and rationale accepted from the implementer.
    ImplementationCandidate? Implementation = null,
    // Evidence produced by normal C# verification.
    VerificationResult? Verification = null,
    // The reviewer's accepted decision.
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
* optional per-agent model request controls;
* optional typed capabilities;
* optional structured output; and
* optional session continuation.

### TypeScript

```ts
const reviewer = agent<State, ReviewDecision>({
    // Routes and ledger entries refer to this stable name.
    id: "reviewer",
    // Keep the role narrow: judge the exact candidate and evidence.
    instructions:
        "Review the exact implementation against the requirements and passing verification evidence.",
    // The host chooses which model performs this role.
    client: clients.reviewer,

    // Build each visit from the latest facts, not hidden conversation state.
    message: (state) =>
        [
            `Requirements: ${JSON.stringify(state.requirements)}`,
            `Exact source: ${state.implementation!.source}`,
            `Passing verification evidence: ${JSON.stringify(state.verification)}`,
        ].join("\n"),

    output: {
        // Ask for the decision the application needs, not arbitrary prose.
        instructions:
            "Return Accept or RequestChanges with a concise summary and concrete findings.",
        // Tandem corrects anything that does not match this shape.
        schema: ReviewDecision,
        // Only an accepted decision is allowed to update state.
        apply: recordReview,
    },
});
```

### C#

```csharp
var reviewer = Agent
    .Create<CodeWriterState>(
        // Routes and ledger entries refer to this stable name.
        "reviewer",
        // Keep the role narrow: judge the exact candidate and evidence.
        "Review the exact implementation against the requirements and passing verification evidence.",
        // The host chooses which model performs this role.
        clients.Reviewer)
    // Build each visit from the latest accepted candidate and checks.
    .WithMessage(state =>
        $"Requirements: {JsonSerializer.Serialize(state.Requirements)}\n"
        + $"Exact source: {state.Implementation!.Source}\n"
        + $"Passing verification evidence: {JsonSerializer.Serialize(state.Verification)}")
    .WithOutput(
        // This definition owns the response shape and validation.
        new ReviewDecisionOutput(),
        // Only an accepted decision is allowed to update state.
        (state, review) => state.RecordReview(review))
    .Build();
```

### Model request controls

Different agent roles can require different model behavior. A reviewer may need repeatable decisions and a bounded
answer while another agent uses the model provider's defaults.

Set those request controls where the role is defined.

### TypeScript

```ts
const reviewer = agent<State, ReviewDecision>({
    id: "reviewer",
    instructions:
        "Review the exact implementation against the requirements.",
    client: {
        ...clients.reviewer,
        // Explicitly ask a compatible model not to spend tokens reasoning.
        reasoningEffort: "none",
    },
    message: reviewerMessage,

    // Prefer repeatable decisions for this role.
    temperature: 0,
    // Bound the complete response, including structured output.
    maxOutputTokens: 4096,

    output: {
        instructions:
            "Return Accept or RequestChanges with concrete findings.",
        schema: ReviewDecision,
        apply: recordReview,
    },
});
```

### C#

```csharp
var reviewer = Agent
    .Create<State>(
        "reviewer",
        "Review the exact implementation against the requirements.",
        clients.Reviewer)
    // Keep one provider-neutral request policy attached to every model turn.
    .WithModelRequestOptions(
        new AgentModelRequestOptions(
            reasoningEffort: AgentReasoningEffort.None,
            // Prefer repeatable decisions for this role.
            temperature: 0,
            // Bound the complete response, including structured output.
            maxOutputTokens: 4096))
    .WithMessage(ReviewerMessage)
    .WithOutput(
        new ReviewDecisionOutput(),
        (state, review) => state.RecordReview(review))
    .Build();
```

Reasoning effort accepts `"none"`, `"low"`, `"medium"`, or `"high"`. Omitting it expresses no Tandem preference.
Temperature must be between `0` and `2`, and maximum output tokens must be a positive 32-bit integer. These settings
remain attached to the agent during capability calls and structured-output correction turns; the selected
OpenAI-compatible endpoint still decides which settings it supports.

These are model request settings, not application facts. They do not belong in state and do not affect routing. The
TypeScript bridge translates its authoring shape into the same C# `AgentModelRequestOptions`; it does not own a second
request-policy implementation.

### Workspace tools

Define a repository environment once, then give each Harness agent an explicit tool set:

```csharp
var repository = AgentWorkspace<State>.Define(
    state => state.WorkspacePath,
    [
        AgentCommand.Define(
            // The model chooses this tool name; the application owns the command text.
            "run_tests",
            "Run the complete test suite.",
            "task test")
    ]
);

var worker = Agent
    .Create<State>("worker", "Implement and verify the requested change.", clients.Worker)
    .UseHarness(harnessInstructions)
    .WithWorkspace(
        repository,
        [
            // Reads and application-declared commands are always available.
            AgentTools.Always<State>(
                "read_file",
                "ls",
                "grep",
                "git:ro",
                repository.Commands),
            // File mutation follows application state, not model preference.
            AgentTools.When<State>(
                state => state.MutationAuthorized,
                "write_file",
                "delete_file",
                "replace",
                "replace_lines")
        ])
    .WithMessage(state => state.Request)
    .Build();
```

Each agent receives only the groups passed to its own `WithWorkspace` call.
`AgentTools.Always` exposes a group on every visit. `AgentTools.When` reevaluates its
predicate from current state before each visit. `"git:ro"` expands to bounded status,
diff, log, show, blame, changed-file, and exact-comparison tools.

`repository.Commands` selects the complete fixed command catalogue. Each command is
a parameterless model tool: the model can choose `run_tests`, but it cannot alter
`task test` or append another argument. Successful command calls can be required by
output acceptance as `ProcessExecution` observations. A later failed call of the same
command invalidates its earlier successful observation.

Fixed commands execute without approval and with the Tandem host process's filesystem
and network authority. They are feedback and evidence for the agent visit; the
application still decides which deterministic stage or route makes verification
authoritative for its lifecycle.

Selecting `"shell"` additionally lets the model author the command text. The workspace
is only the starting directory for either form of process execution; it is not
filesystem or network isolation. Omit `"shell"` when the agent should be limited to
application-declared commands.

## Typed model output becomes application state

Tandem's structured outputs and capabilities give that boundary an application-owned type. For
example, Code Writer does not ask the Reviewer for arbitrary prose and then ask another model what that prose means.

### TypeScript

```ts
export const ReviewDecision = z
    .object({
        // The graph only needs one of these two decisions.
        decision: z.enum(["Accept", "RequestChanges"]),
        // Give the caller a concise account of the review.
        summary: z.string().min(1),
        // Requested changes must say exactly what needs fixing.
        findings: z.array(z.string().min(1)),
    })
    // Do not allow an empty RequestChanges response into state.
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
    // The candidate may leave the graph successfully.
    Accept,
    // The candidate needs another implementer turn.
    RequestChanges,
}

public sealed record ReviewDecision(
    // Routes use this value to finish or loop.
    ReviewDisposition Decision,
    // The caller can show this account directly.
    string Summary,
    // Requested changes carry concrete work for the next turn.
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
    // This becomes the function name exposed to the implementer.
    name: "submit_implementation",
    // Tell the model what a complete call must contain.
    instructions:
        "Submit the complete JavaScript implementation and its rationale.",

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

Attach it to the intended agent. Agents start with a fresh session on each visit; the implementer keeps its session
because verification or review can route work back to it:

```ts
const implementer = agent<State>({
    // This identity stays stable when the graph loops back.
    id: "implementer",
    // These instructions remain the same on every visit.
    instructions: "Implement the requested function.",
    // The host supplies the model used for implementation.
    client: clients.implementer,
    // Each turn is grounded in the latest application state.
    message: implementerMessage,
    // Submitting an implementation is the only action this agent may take.
    capabilities: [submitImplementation],
    // Preserve its conversation when verification or review sends work back.
    continueSession: true,
});
```

### C#

In C#, the capability definition owns its semantic contract:

```csharp
public sealed class SubmitImplementationCapability
    : IAgentCapabilityDefinition<CodeWriterState, SubmitImplementation>
{
    // This is the function name exposed to the model.
    public string ToolName => "submit_implementation";

    // Tell the model what a complete call must contain.
    public string Instructions =>
        "Submit the complete JavaScript implementation and its rationale.";

    // Reject invalid calls before application code sees them.
    public IValidator<SubmitImplementation> Validator { get; } =
        new SubmitImplementationValidator();

    // Keep the accepted call readable in observations and the ledger.
    public string Summarize(SubmitImplementation request) =>
        $"Implementation:\n{request.Implementation}\n\nRationale:\n{request.Rationale}";
}
```

Then bind its accepted request to a typed state transition:

```csharp
var submitImplementation =
    AgentCapabilities.Create<CodeWriterState, SubmitImplementation>(
        // Reuse the function name, instructions, validation, and summary above.
        new SubmitImplementationCapability(),
        // An accepted call records the candidate and clears stale checks.
        (state, submission) =>
            state.RecordImplementation(submission));
```

And attach it to the agent:

```csharp
var implementer = Agent
    .Create<CodeWriterState>(
        // This identity stays stable when the graph loops back.
        "implementer",
        // These instructions remain the same on every visit.
        "Implement the requested function.",
        // The host supplies the model used for implementation.
        clients.Implementer)
    // Each turn is grounded in the latest application state.
    .WithMessage(ImplementerMessage)
    // Submitting an implementation is the only action it may take.
    .WithCapability(submitImplementation)
    // Preserve its conversation when the graph sends work back.
    .ContinueSession()
    .Build();
```

An accepted capability call concludes that agent visit. The updated state is then handed back to the pipeline, which
evaluates the configured routes.

### Agent Skills

Applications can attach an existing Agent Skills or OpenCode skill directory to a specific agent:

```csharp
var meridian = AgentSkill.FromDirectory(
    "/Users/max/.claude/skills/meridian");

var reviewer = Agent
    .Create<ReviewState>("reviewer", "Use the meridian skill to review the design.", client)
    .WithSkill(meridian)
    .WithMessage(state => state.Request)
    .Build();
```

The selected directory must contain `SKILL.md`. Microsoft Agent Framework owns progressive disclosure,
`load_skill`, and read-only resource access such as `references/*.md`. Tandem never scans the current working
directory, an agent workspace, OpenCode configuration, or home directories for skills.

Skills are instruction packages, not capabilities: attaching one grants no state transition, lifecycle action,
or workspace mutation authority. File scripts are filtered from the source and cannot execute. MAF currently
advertises its approval-gated `run_skill_script` tool even when no scripts are available.

## Stages

Stages use the same state as agents and are routed in exactly the same graph.

Code Writer's verification step is a stage because testing the submitted implementation does not require another model.

### TypeScript

```ts
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

### C#

```csharp
[PipelineStage("verification")]
public sealed partial class VerificationStage
{
    // The stage owns the normal C# service that performs the check.
    private readonly ImplementationAssessment _assessment = new();

    public async ValueTask<CodeWriterState> ExecuteAsync(
        CodeWriterState state,
        CancellationToken cancellationToken)
    {
        // Verification cannot run until an implementation has been accepted.
        var source =
            state.Implementation?.Source
            ?? throw new InvalidOperationException(
                "Verification requires an implementation.");

        // Execute the check without involving another model.
        var verification =
            await _assessment.AssessAsync(source, cancellationToken);

        // Return the same state with the new evidence recorded.
        return state.RecordVerification(verification);
    }
}
```

A stage can perform whatever operation belongs at that point in the lifecycle: validation, calculation, database work,
an API call, compilation, verification, transformation, or another deterministic application operation. It does not need
to know who runs before or after it.

## Parallel work

When several operations depend on the same accepted facts but not on one another, a parallel group can run them together
and explicitly combine what each learned.

Every named branch receives isolated state. All branches run concurrently and must succeed before merge runs. Merge
receives the original baseline and each branch's resulting state, so application code decides how the facts combine rather
than allowing completion order to decide.

### TypeScript

```ts
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
        route({
            from: classify,
            outcome: "success",
            to: done,
            label: "classification complete",
        }),
        // A declared branch failure skips merge.
        route({
            from: classify,
            outcome: "failed",
            to: failed,
            label: "classification failed",
        }),
    ],
    outputs: [done, failed],
});
```

### C#

```csharp
var classify = PipelineNodes.Parallel(
    // The parent graph routes through this one semantic participant.
    id: "classify-framing",

    // Give every branch its own application-state graph.
    clone: state => state with
    {
        Findings = [.. state.Findings],
    },

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
        }
);

var pipeline = Pipeline
    .Start(classify, "classify-framing")
    // Continue only after every branch succeeds and merge completes.
    .Route(classify.Success, done, "classification complete")
    // A declared branch failure follows the normal failed outcome.
    .Route(classify.Failed, failed, "classification failed")
    .Build(done, failed);
```

Branches may be agents or stages. Terminals, interactions, nested parallel groups, and branch subgraphs are not supported.
Branch participants belong to the group and cannot also appear elsewhere in the parent graph. Branch observations can
arrive in any order, and external side effects are not rolled back if a sibling fails. Caller cancellation reaches active
branches.

C# clone logic must isolate every mutable application object that branch code can reach. TypeScript receives that
isolation through its validated JSON boundary. When persistence is enabled, branch results remain accepted under the
branch participant IDs and the merged state is accepted under the parallel group ID.

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
    // The graph pauses at this named handoff.
    id: "customer-reply",
    // Validate both what leaves the pipeline and what comes back.
    requestSchema: CustomerQuestion,
    responseSchema: CustomerReply,

    // Build the question from the latest support facts.
    request: (state) => state.createCustomerQuestion(),

    // Turn the accepted reply back into application state.
    apply: (state, reply) =>
        state.recordCustomerReply(reply),
});
```

A host supplies the handler separately:

```ts
const handlers = interactions().handle(
    customerReply,
    // The host decides how this request reaches the customer.
    async (question) => askCustomer(question),
);

const result = await run(support, initialState, {
    // Bind this live channel only for this run.
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
        // The graph pauses at this named handoff.
        "customer-reply",
        // Build the question from the latest support facts.
        state => state.CreateCustomerQuestion(),
        // Turn the accepted reply back into application state.
        (state, reply) => state.RecordCustomerReply(reply));
```

Interactions are live and process-owned; they do not make stopped runs resumable.

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
    // Give the whole lifecycle one name in logs and the ledger.
    name: "code-writer",
    // Every node reads and returns this same state shape.
    state: State,

    // List everything that can take a turn or finish the run.
    nodes: [
        implementer,
        verification,
        reviewer,
        done,
        failed,
    ],

    // The implementer receives the initial state first.
    start: implementer,

    routes: [
        // A completed capability call gives verification a candidate to check.
        route({
            from: implementer,
            to: verification,
            outcome: "success",
            label: "implementation submitted",
        }),

        // If the implementer itself fails, there is no candidate to verify.
        route({
            from: implementer,
            to: failed,
            outcome: "failed",
            label: "implementer failed",
        }),

        // Passing checks move the exact candidate and evidence to review.
        route({
            from: verification,
            to: reviewer,
            when: (state) =>
                state.verification?.passed === true,
            label: "verification passed",
        }),

        // Failed checks send their evidence back to the same implementer.
        route({
            from: verification,
            to: implementer,
            when: (state) =>
                state.verification?.passed === false,
            label: "verification failed",
        }),

        // Requested changes are valid output, so they loop rather than fail.
        route({
            from: reviewer,
            to: implementer,
            outcome: "success",
            when: (state) =>
                state.review?.decision === "RequestChanges",
            label: "changes requested",
        }),

        // Accept is the application fact that finishes the work successfully.
        route({
            from: reviewer,
            to: done,
            outcome: "success",
            when: (state) =>
                state.review?.decision === "Accept",
            label: "accepted",
        }),

        // A reviewer fault is different from a RequestChanges decision.
        route({
            from: reviewer,
            to: failed,
            outcome: "failed",
            label: "reviewer failed",
        }),
    ],

    // These are the only places the run may finish.
    outputs: [done, failed],
    // Keep accepted values so this run can be inspected later.
    persist: true,
});
```

### C#

```csharp
public Pipeline<CodeWriterState> Build() =>
    Pipeline
        // Begin with the implementer and name the whole lifecycle.
        .Start(
            at: codeWriter.Implementer,
            name: "code-writer",
            description:
                "Implement and verify a function until review accepts it."
        )
        // A completed capability call gives verification a candidate to check.
        .Route(
            on: codeWriter.Implementer.Success,
            to: codeWriter.Verification,
            label: "implementation submitted"
        )
        // If the implementer itself fails, there is no candidate to verify.
        .Route(
            on: codeWriter.Implementer.Failed,
            to: codeWriter.Failed,
            label: "implementer failed"
        )
        // Passing checks move the exact candidate and evidence to review.
        .Route(
            from: codeWriter.Verification,
            when: state =>
                state.Verification?.Passed is true,
            to: codeWriter.Reviewer,
            label: "verification passed"
        )
        // Failed checks send their evidence back to the same implementer.
        .Route(
            from: codeWriter.Verification,
            when: state =>
                state.Verification?.Passed is false,
            to: codeWriter.Implementer,
            label: "verification failed"
        )
        // Requested changes are valid output, so they loop rather than fail.
        .Route(
            on: codeWriter.Reviewer.Success,
            when: state =>
                state.Review?.Decision
                    == ReviewDisposition.RequestChanges,
            to: codeWriter.Implementer,
            label: "changes requested"
        )
        // Accept is the application fact that finishes the work successfully.
        .Route(
            on: codeWriter.Reviewer.Success,
            when: state =>
                state.Review?.Decision
                    == ReviewDisposition.Accept,
            to: codeWriter.Complete,
            label: "accepted"
        )
        // A reviewer fault is different from a RequestChanges decision.
        .Route(
            on: codeWriter.Reviewer.Failed,
            to: codeWriter.Failed,
            label: "reviewer failed"
        )
        // Keep accepted values so this run can be inspected later.
        .Persist()
        // A run can leave the graph only through these two outputs.
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

A parallel participant runs its named branches concurrently, merges successful branch states, and then returns one
ordinary outcome to this same route cycle.

There is no second application-level orchestration model behind the graph.

The configured pipeline is the lifecycle.

## Outputs

A successful output and a failed output are distinct, inspectable destinations rather than implicit conventions.

### TypeScript

```ts
const done = output<State>({
    // Accepted reviews route to this successful endpoint.
    id: "done",
    // Return the reviewer's own concise account to the caller.
    summary: (state) => state.review!.summary,
});

const failed = output<State>({
    // Agent faults route to a separate endpoint.
    id: "failed",
    // Tell the host that reaching this output means the run failed.
    failed: true,
    // Give the caller a useful result without exposing runtime internals.
    summary: () =>
        "An agent failed before the code could be accepted.",
});
```

### C#

```csharp
// Accepted reviews finish here.
var complete = PipelineNodes.Complete(new CodeWriterComplete());

// Agent faults finish somewhere explicitly unsuccessful.
var failed = PipelineNodes.Failed(new CodeWriterFailed());
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

Tandem records a value when the stage, agent, capability, or interaction accepts it.

For example:

```ts
const recordResult = stage<State>({
    // Use this name to find the accepted value later.
    id: "record-result",
    // Record the state returned when this stage succeeds.
    persist: true,

    execute: (state) => ({
        // Keep every fact already known by the application.
        ...state,
        // Add the smaller result the caller cares about.
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

Applications read accepted values through `inspectAccepted` in TypeScript or
`SqliteLedgerStore` in C#. The application owns the ledger path and any operator-facing
inspection interface.

## Running a pipeline

Your application owns the process and starts a pipeline with its initial state.

### TypeScript

```ts
import { run } from "@maxanstey-meridian/tandem";

const result = await run(
    // Run this configured lifecycle...
    codeWriter,
    // ...starting from these application facts...
    initialState,
    {
        // Let the caller cancel a run that takes too long.
        signal: AbortSignal.timeout(180_000),
        // Supply a ledger only when this pipeline persists values.
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
        // Run this configured lifecycle...
        codeWriter,
        // ...starting from these application facts...
        initialState,
        // Tandem owns the SQLite run and persistence observer lifecycle.
        new SqlitePipelineRunOptions("code-writer.sqlite3"),
        // ...until completion or caller cancellation.
        cancellationToken);

Console.WriteLine(result.Status);
Console.WriteLine(result.State);
```

`SqlitePipelineRunOptions` creates and terminalises the ledger run and supplies the persistence observer. Custom hosts
can still compose observers directly through `PipelineRunOptions` when they need lower-level control.

## C# and TypeScript

Tandem has one execution model with two authoring surfaces.

|                       | C#                                            | TypeScript                            |
|-----------------------|-----------------------------------------------|---------------------------------------|
| **State**             | Normal typed application state                | Zod schema + inferred TypeScript type |
| **Stages**            | Generated from `[PipelineStage]` classes      | `stage(...)`                          |
| **Agents**            | `Agent.Create<TState>(...)`                   | `agent<TState>(...)`                  |
| **Parallel groups**   | `PipelineNodes.Parallel(...)`                 | `parallel(...)`                       |
| **Capabilities**      | Typed definitions + validators                | Zod request schemas                   |
| **Structured output** | Typed output definitions + validators         | Zod output schemas                    |
| **Interactions**      | `PipelineNodes.WaitFor<...>`                  | `interaction(...)`                    |
| **Routes**            | Fluent `Pipeline.Route(...)`                  | `route(...)`                          |
| **Runtime**           | Tandem + Microsoft Agent Framework in-process | The same Tandem/.NET engine           |


TypeScript applications import `@maxanstey-meridian/tandem`; they do not build or manually load .NET assemblies.

See [`typescript/README.md`](typescript/README.md) for TypeScript-specific runtime and packaging details.

## Packet files

The optional packet packages decode Markdown plus YAML frontmatter at the application boundary. The application owns the packet type, validation, and explicit conversion into state; reading a packet does not configure or start a pipeline.

```csharp
using Tandem.Packets;

var input = PacketFile.Read<WorkPacket>(path);
var state = WorkState.Create(input.Value, input.Context, input.Source);
```

```ts
import { readPacketFile } from "@maxanstey-meridian/tandem-packets";

const input = await readPacketFile(path, WorkPacket.strict());
const state = createWorkState(input.value, input.context, input.source);
```

```text
packet file -> validated application packet -> application state -> pipeline run
```

In TypeScript, the caller-owned Zod schema controls unknown-field rejection; use a strict object schema when packet frontmatter must reject unknown fields.

## Microsoft Agent Framework

Tandem owns the application-facing model:

* typed state;
* participants;
* agent definitions;
* named parallel branches and deterministic merge;
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

Start with the package-backed [C# quickstart](docs/quickstarts/csharp.md) or
[TypeScript quickstart](docs/quickstarts/typescript.md), then follow the
[getting-started progression](examples/getting-started) from one participant through routing,
deterministic stages, and persistence.

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

Code Writer also requires Node.js for its JavaScript verifier. After the run finishes,
press `q` to close the terminal view and print its run ID and absolute ledger path.
TypeScript applications can inspect that ledger with `inspectAccepted`; C# applications
can query it with `SqliteLedgerStore`.

## Documentation

For more detail, see:

* [`typescript/README.md`](typescript/README.md) — TypeScript SDK requirements, packages, chat clients, persistence, and
  development; and
* [`CONTRIBUTING.md`](CONTRIBUTING.md) — architecture boundaries and invariants for contributors.

## License

[MIT](LICENSE)
