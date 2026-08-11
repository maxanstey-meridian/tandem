# Contributing to Tandem

## Architecture

Tandem is a typed agentic pipeline SDK over MAF's live execution engine.
Applications consume Tandem through the same public package boundary. The runtime
vocabulary is:

- **Step**: one executable pipeline operation.
- **Stage**: one deterministic step.
- **Agent**: one model-backed step.
- **State**: composition-owned lifecycle facts for the live run.
- **Runtime**: composition-neutral session, usage, invocation, and run bookkeeping.
- **Outcome**: the result emitted by a step.
- **Condition**: an ordinary predicate over typed state, or an explicitly
  Advanced predicate over narrow execution evidence.
- **Route**: an ordered condition and destination pair.
- **Prompt**: instructions contributed to an agent step.

The execution cycle is: run a step, publish its observations and result to the
current run observer when configured, evaluate its routes in order, then run the
first matching destination, suspend, or complete.

## Core Tandem Invariants

Preserve these invariants when designing any new abstraction.

Tandem is a typed agentic pipeline SDK, not a workflow engine, Harness, or
orchestration framework exposed to users. Agents, ordinary C# stages, and human
interactions are first-class pipeline participants. Agents are modeled nodes in
the graph, not arbitrary services that application code calls. MAF, provider APIs,
function-calling and MCP transport, executor bindings, persistence mechanics, and
other runtime machinery belong below the ordinary public seam.

`TState` contains application facts only. If another participant needs to know
something, represent it strongly and explicitly in state. Do not use state to
smuggle graph or runtime information such as node IDs, source-step IDs, run IDs,
invocation IDs, latest-outcome blobs, serialized payloads, or resume positions.
Application facts belong in state; control flow belongs in the graph; execution
facts belong in Tandem's runtime.

Capabilities are typed semantic actions. A capability describes something the
agent is permitted to do in the application's language: typed request,
validation, then typed state transition. Function calling, MCP, receipts, and
tool transport are implementation details. An accepted capability transition
concludes that agent visit. Do not turn capabilities into an unbounded generic
tool bag unless a demonstrated application use case requires a separate tool
concept.

Routing is explicit and semantic. `Success` and `Failed` describe whether a node
executed successfully. Domain meaning is represented in typed state, and routes
inspect that state to select the next participant. Human interaction is one
first-class typed `WaitFor<TRequest, TResponse>` node with its own semantic
identity; the host decides whether the human channel is a CLI, web UI, another
application, or an external service. Ordinary runs are process-owned. Do not
accidentally rebuild generalized durability.

Core versus Advanced is a semantic boundary, not a complexity tier. Core APIs
describe what the application and its agents mean: state, agents, outputs,
capabilities, routes, and interactions. `Tandem.Advanced` is only for deliberately
participating in how Tandem executes those concepts: narrow runtime context,
invocation identity, provider options, observation plumbing, workspace authority,
and low-level policy hooks. A sophisticated agent can remain entirely in Core.

When adding an abstraction, apply this placement test: could an application
author reasonably invent the concept while describing their system without
knowing how Tandem is implemented? If yes, it may belong in Core. If it exists
because of Tandem, MAF, provider, persistence, or transport machinery, keep it
private or Advanced. If a feature forces ordinary authoring back into JSON
parsing, string outcome kinds, runtime envelopes, node identities, or
transport-specific concepts, assume the abstraction is at the wrong level and
redesign the seam rather than teaching users the machinery.

Application pipelines obey the same rules. They are consumers built with Tandem,
not privileged second frameworks. Their complexity reduces to typed application
state, agent nodes, capabilities, ordinary stages, explicit routes, and typed
human waits. Only genuinely operational concerns cross into Advanced.

The shorthand is:

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

## Boundaries

Keep these ownership boundaries explicit:

- Pipeline composition owns step order, prompts, profiles, conditions, and
  successors.
- Pipeline composition owns its concrete `TState`, user messages, workspace
  policy, structured mappings, and directly attached capability set.
- Ordinary authored steps and policies use concrete `TState`; core must not add a
  universal lifecycle state interface or state bag. Advanced APIs expose narrow
  context and operation values; the complete execution envelope remains internal.
- Envelope-aware agent policy, raw parsing, checkpoint mechanics, Harness selection,
  and runtime observation are extension methods in `Tandem.Advanced`, not
  ordinary `AgentBuilder<TState>` instance methods.
- Core capability authors own typed requests, validation, summaries, and state
  transitions. Advanced may decorate that same capability with asynchronous
  runtime-aware acceptance. Tandem owns invocation identity, local MAF tool binding,
  and atomic accepted-call ownership.
- Steps own operations, not orchestration.
- Pipeline state records facts; it does not hide routing logic.
- Microsoft Agent Framework owns workflow execution, sessions,
  model loops, tool dispatch, and workflow events.
- An application owns its composition, participants, conditions, policies,
  infrastructure operations, and operator interfaces.
- Applications decide which agents receive read-only repository inspection,
  fixed commands, unrestricted shell access, or conditional mutation tools.
- Application state and routes establish semantic authority; Advanced workspace
  policy enforces the corresponding runtime tool boundary.

Do not introduce a second orchestration engine, imperative lifecycle coordinator,
or application-level agent loop. A lifecycle change belongs in workflow
composition unless it changes what a participant operation itself does.

## Machine Boundaries

Treat all model-authored data as untrusted boundary input.

- Local capabilities compose their typed request contract, validator, flat
  schema, summary, and asynchronous acceptance callback.
- Invalid capability calls return structured tool errors before acceptance or
  state transition and may be corrected in the same MAF session.
- One capability call atomically owns acceptance. Durable acceptance completes
  before state transition, mechanical turn termination, or workflow routing.
- An accepted capability concludes the current agent visit. Structured-output
  parsing and correction run only when no capability was accepted.
- Harness tool authority is classified from maintained SDK constants in Advanced.
  Delivery gates semantic effects, never guessed name prefixes; unclassified tools
  fail closed wherever an authority gate is active.
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
`Action`, `Policies`, `Prompts`, `Decision`, `Composition`, `Participants`, `Result`,
`State`, or `Registration`. Examples include `ReviewerAgent`,
`VerificationStage`, `HumanInputPort`, `SubmitReportAction`,
`DeliveryComposition`, and `DeliveryParticipants`.

`Participants` is the typed authored inventory used by composition. It is not a
second graph: composition still registers every route directly with MAF through
Tandem. Keep the pipeline spine at the package root and group optional concerns
under `Agents`, `Capabilities`, `Stages`, `Interactions`, `Observation`, and
`Infrastructure`. Add only the folders whose concepts exist; small pipelines may
keep their authored parts beside the root spine.

The generator infers pass-through, state-updating, or standard `Outcome<TState>`
authoring from `ExecuteAsync`. Use only standard `Success` and `Failed` as step
  outcomes; put domain decisions in typed pipeline state and route with state
predicates. Do not introduce pipeline-wide ad hoc string results, custom result
unions, or vague `Manager`, `Service`, `Processor`, `Handler`, `Provider`, or
`Helper` names where a precise pipeline role exists.

Composition is the complete route map. Route calls immediately add real MAF edges
and never retain a second execution graph. Inspection projects that executable graph
into Tandem's semantic nodes and routes; Mermaid and DOT render the same projection
without private MAF expansion.

Use `.Route(on: step, to: next)` for unconditional serial flow and
`.Route(on: step.Success, when: state => ..., to: next)` for semantic branching.
Ordinary route predicates receive `TState`. Do not mix unconditional and
outcome-specific routes from one source.

## Composition Test

The design is healthy when composition changes alone can:

- remove or insert planner and reviewer agents;
- reorder verification commands;
- route a failed command to a configured remediation agent;
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

The ordinary C# authoring API is the semantic source of truth for the TypeScript
authoring layer. TypeScript may adapt syntax, validation, and transport across the
bridge, but it must not own application-facing pipeline or agent semantics that a
native C# application cannot express through Tandem's public API. CLR dependency
descriptors needed only to construct an adapter, such as an OpenAI-compatible
client endpoint, remain transport concerns rather than authoring semantics.

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
