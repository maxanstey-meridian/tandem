# Contributing to Tandem

## Architecture

Tandem is the engine, Delivery is the flagship first-party pipeline, and custom
pipelines are unprivileged consumers. The runtime vocabulary is:

- **Step**: one executable pipeline operation.
- **Stage**: one deterministic step.
- **Agent**: one model-backed step.
- **State**: composition-owned lifecycle facts for the live run.
- **Runtime**: composition-neutral session, usage, invocation, and run bookkeeping.
- **Outcome**: the result emitted by a block.
- **Condition**: a predicate over the typed pipeline message and latest outcome.
- **Route**: an ordered condition and destination pair.
- **Prompt**: instructions contributed to an agent step.

The execution cycle is: run a step, publish its observations and result to the
current run observer when configured, evaluate its routes in order, then run the
first matching destination, suspend, or complete.

## Boundaries

Keep these ownership boundaries explicit:

- Pipeline composition owns block order, prompts, profiles, conditions, and
  successors.
- Pipeline composition owns its concrete `TState`, user messages, workspace
  policy, structured mappings, and directly attached capability set.
- Ordinary authored steps and policies use concrete `TState`; core must not add a
  universal lifecycle state interface or state bag. Advanced APIs expose narrow
  context and operation values; the complete execution envelope remains internal.
- Envelope-aware agent policy, raw parsing, checkpoint mechanics, capabilities,
  and runtime observation are extension methods in `Tandem.Advanced`, not
  ordinary `AgentBuilder<TState>` instance methods.
- Capability authors own typed requests, validation, summaries, and state
  transitions, including asynchronous durable acceptance when required. Tandem
  owns invocation identity, local MAF tool binding, and atomic accepted-call
  ownership.
- Steps own operations, not orchestration.
- Pipeline state records facts; it does not hide routing logic.
- Microsoft Agent Framework owns workflow execution, sessions,
  model loops, tool dispatch, and workflow events.
- Tandem owns product composition, blocks, conditions, policies, Git and
  verification operations, event projection, and operator interfaces.
- Planner and reviewer blocks have read-only workspace access.
- Executor mutation is available only after the pipeline establishes mutation
  authority.
- Each run operates in an isolated clone pinned to the resolved base commit.
- Review is grounded in the exact candidate captured and verified by the
  pipeline.

Do not introduce a second orchestration engine, imperative lifecycle coordinator,
or application-level agent loop. A lifecycle change belongs in workflow
composition unless it changes what a block operation itself does.

## Machine Boundaries

Treat all model-authored data as untrusted boundary input.

- Local capabilities compose their typed request contract, validator, flat
  schema, summary, and asynchronous acceptance callback.
- Invalid capability calls return structured tool errors before acceptance or
  state transition and may be corrected in the same MAF session.
- One capability call atomically owns acceptance. Durable acceptance completes
  before state transition, mechanical turn termination, or workflow routing.
- Planner and reviewer structured output must pass syntax, shape, enum, and
  cross-field validation before it can affect context or routing.
- Structured-output recovery gets one corrective response in the same agent
  session, then fails closed with the raw response and validation problems.
- Runtime FluentValidation rules are authoritative where generated JSON Schema
  cannot express semantic constraints.

Capability execution follows direct agent attachment through MAF function
middleware. Do not add tool-name switches, DI discovery, or duplicate semantic
validation inside handlers.

## Naming Grammar

Use a semantic name followed by one role suffix: `Agent`, `Stage`, `Port`,
`Action`, `Policies`, `Prompts`, `Decision`, `Composition`, `Steps`, `Result`,
`State`, or `Registration`. Examples include `ReviewerAgent`,
`VerificationStage`, `HumanInputPort`, `SubmitReportAction`,
`DeliveryComposition`, and `DeliverySteps`.

The generator infers pass-through, state-updating, or standard `Outcome<TState>`
authoring from `ExecuteAsync`. Use only standard `Success` and `Failed` as step
  outcomes; put domain decisions in typed pipeline state and route with state
predicates. Do not introduce pipeline-wide ad hoc string results, custom result
unions, or vague `Manager`, `Service`, `Processor`, `Handler`, `Provider`, or
`Helper` names where a precise pipeline role exists.

Composition is the complete route map. Route calls immediately add real MAF edges
and never retain route descriptors, a graph AST, staged route state, or a parallel
renderer. Inspection reflects the built MAF workflow and delegates Mermaid/DOT
export to MAF.

Use `.Route(on: step, to: next)` for unconditional serial flow and
`.Route(on: step.Success, when: state => ..., to: next)` for semantic branching.
Ordinary route predicates receive `TState`. Do not mix unconditional and
outcome-specific routes from one source.

## Composition Test

The design is healthy when composition changes alone can:

- remove or insert planner and reviewer blocks;
- reorder verification commands;
- route a failed command to a configured remediation block;
- contribute prompts to selected agents;
- rotate sessions or promote models under configured conditions; and
- preserve accepted side effects across live agent turns.

The decisive rule is:

```text
The configured pipeline is the lifecycle.
The runtime executes it in the initiating process.
```

## Development

Prefer the maintained framework or SDK for commodity behavior and add only the
machinery required by Tandem's product boundary. Keep changes small and prove
behavior through public interfaces and end-to-end execution paths.

Run the repository checks before submitting changes:

```sh
dotnet tool restore
task check
```

Runtime tests use real MAF in-process execution rather than a mocked event loop.

Public package boundaries are also proven through packed consumers. Changes to
exported types require a deliberate update to the owning `ExportedApi.txt`.
Generator ABI remains hidden with `EditorBrowsable(Never)`, and the analyzer must
remain under `analyzers/dotnet/cs` in `Tandem.Generators`. `task check` runs the
package restore, execution, analyzer-delivery, and dependency-isolation proof.
