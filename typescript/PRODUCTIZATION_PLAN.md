# TypeScript SDK Experimental Productization Plan

Status: phases 1-7 implemented as a macOS arm64 product experiment; bounded phase-8
automated gates and the real two-agent dogfood pass. The deferred phase-8 matrix
remains open, and this is not a stable-product commitment.

## Implementation Evidence (2026-08-09)

- Phase 4: `packages/sdk` is the sole public facade; all Zod boundaries are
  validation-only and structured validation problems retain paths.
- Phase 5: the C# host owns versioned OpenAI-compatible client adapters and .NET
  `IChatClient` construction; optional model discovery is deterministic. There are
  no model callbacks or vendor model npm dependencies.
- Phase 6: `packages/runtime` selects an optional RID package without coupling the
  SDK to one platform. `packages/runtime-darwin-arm64` contains generated
  JS/declarations, runtimeconfig, an allowlisted managed runtime closure, and one
  arm64 SQLite asset. Packed consumers load package-relatively without a source
  checkout or consumer build.
- Phase 7: `sample/src` dogfoods an honest executable-code loop using the same
  semantic ownership language as Delivery, scaled to this domain. One state module
  owns cohesive application fact contracts and pure transitions; complete agent,
  capability, and verification declarations own their local semantics. Executable
  source remains a string through acceptance and is evaluated by a bounded child
  adapter whose worker owns the callable Zod transform. The lexical
  composition root constructs every participant and visibly declares every route,
  while the runner alone owns concrete clients and process mechanics. The loop retains explicit failure,
  revision, and acceptance routes, continued implementer sessions, persistence, and accepted-value
  inspection, with no interaction, planner, runtime bookkeeping, or workspace mechanics.
- Live dogfood: OpenRouter `deepseek/deepseek-v4-flash-0731` accepted and applied the
  implementer capability; the verified local `gpt-5.6-sol` reviewer returned accepted
  structured output; SQLite inspection returned the capability,
  output, and state transitions in acceptance order.
- Phase 8: automated tests cover validation, local-tarball package install/content
  and real packaged execution, participant runtime identity, lifecycle status and
  persistence, protocol-faithful reviewer request progress, capability message
  transport, and a deterministic implementation-verification-review loop through real C#
  `IChatClient` adapters. They also cover concurrency, cancellation, callback
  failure, repeated runs in one loaded host, and a bounded 25-run soak. Long soak, abandonment,
  persistence-failure injection, and the complete provider failure/correction
  matrix remain deferred.
- Decision: continue bounded dogfooding. The covered automated gates expose no stop
  criterion; this is not a conclusion about the deferred matrix.

## Goal

Ship one dogfoodable TypeScript package that authors and runs a real Tandem
pipeline through the existing .NET/MAF runtime without exposing CLR or Node API
plumbing.

The first slice must support:

- Zod-backed validation-only state, agent output, capability input, and interaction
  contracts;
- ordinary async stages;
- agents with structured output;
- capabilities;
- human interactions;
- explicit and conditional routes;
- success and failure outputs;
- session, timeout, and persistence policy;
- SQLite accepted-value history; and
- sample-local implementer and reviewer agents, each carrying an OpenAI-compatible chat-client declaration.

Target platform: macOS arm64. Other RIDs remain out of scope until dogfooding is
successful.

## Product Boundary

TypeScript owns application meaning:

- state facts;
- participant declarations;
- route predicates;
- messages and state transitions;
- schemas and application validation; and
- opaque client declarations attached directly to agents.

Tandem and MAF remain authoritative for:

- graph execution;
- route evaluation order;
- agent loops and sessions;
- capability permission and dispatch;
- interaction suspension and resumption;
- correction and acceptance;
- observation ordering; and
- persistence semantics.

The packaged .NET host owns provider transport. It resolves versioned adapter descriptors into
`Microsoft.Extensions.AI.IChatClient` instances and supplies them to Tandem/MAF.
The TypeScript package must not introduce a vendor model SDK or proxy model calls
through JavaScript callbacks.

The bridge may register participants, marshal JSON, dispatch callbacks onto Node's
thread, translate cancellation/errors, load runtime assets, and own host lifecycle.
It must not become a second coordinator.

## Proven Foundation

The current spike proves:

- normal TypeScript inference, structural state, object spread, literals, async,
  mapped types, and negative compile cases;
- registered graph construction and deterministic stage/terminal execution through
  `PipelineRunner` and MAF, with the full live sample remaining an external gate;
- Zod-generated runtime JSON Schema;
- Core JSON correction/acceptance behavior exercised by Tandem tests; deterministic
  Node protocol correction coverage remains part of the deferred matrix;
- success/failed routes, multiple outputs, persistence overrides, sessions, and
  timeouts;
- SQLite journal creation;
- concurrent runs, cancellation, and exception translation; and
- one graph-registration protocol replacing bespoke run entry points.

## Work Plan

### 1. Harden Core JSON Contracts

- Keep `WithJsonOutput` and `CreateJson` additive and provider-neutral.
- Add focused .NET tests for malformed JSON, schema lifetime, validation,
  correction, contextual validation, acceptance ordering, persistence payloads,
  cancellation, and conflicting capability calls.
- Review public names and manifests before treating the API as mergeable.
- Keep typed and JSON capabilities on the shared acceptance runtime.

Exit: Core tests prove the dynamic seams independently of Node API.

### 2. Version The Registration Contract

- Replace ad hoc private records with a versioned bridge contract.
- Validate unknown node kinds, required callback IDs, duplicate IDs, invalid routes,
  outputs, policies, and unsupported versions before constructing a graph.
- Keep the v3 callback registry bridge-local; registration references callback IDs but
  carries no redundant top-level callback manifest. Missing registry entries fault at invocation.
- Keep callback IDs and serialized state below the TS authoring seam.

Exit: malformed registrations fail deterministically before MAF execution.

### 3. Refactor The Bridge Package

- Create focused adapters for stages, interactions, output agents, capability
  agents, terminals, observations, and host providers.
- Centralize JS-thread dispatch, run/error translation, assembly loading, native
  asset resolution, and lifecycle handling.
- Remove diagnostic fallback clients and proof-only branches.
- Keep bridge responsibilities mechanical and auditable.

Exit: adding a participant kind requires one adapter, registration contract data,
and tests rather than changes throughout the bridge.

### 4. Build `@maxanstey-meridian/tandem`

- Turn the inferred probe facade into a package with stable internal ownership.
- Hide graph JSON, callback IDs, DLL paths, Node API types, CLR generics,
  `ValueTask`, and descriptors.
- Use Zod as the initial contract source and preserve inferred state/response types.
- Retain compile-time negative tests for state compatibility. Enforce concrete
  participant membership and identity at pipeline construction; do not claim
  compile-time pipeline ownership.
- Settle names only after the dogfood sample is complete.

Exit: application code imports only `@maxanstey-meridian/tandem`, Zod, and its provider package.

### 5. Wire The Real Tandem Client Adapters

- Build OpenAI-compatible `IChatClient` instances in the packaged .NET host using
  the same maintained .NET transport and `Microsoft.Extensions.AI` seam as Tandem.
- Attach an OpenAI-compatible declaration to the sample implementer for OpenRouter completions at
  `https://openrouter.ai/api/v1`, model
  `deepseek/deepseek-v4-flash-0731`, using `OPENROUTER_API_KEY`.
- Attach an OpenAI-compatible declaration to the sample reviewer for the local Responses-compatible endpoint at
  `http://127.0.0.1:10531/v1`, model `gpt-5.6-sol`, low reasoning, and no API key.
- Before reviewer execution, query `/v1/models` and fail with an actionable message
  unless it exposes `gpt-5.6-sol`.
- Preserve cancellation, usage, finish reasons, structured response formats, tools,
  and provider errors through the .NET `IChatClient` implementation.
- Keep the TypeScript surface limited to declaring an opaque client descriptor. It must not
  receive model messages, execute model HTTP calls, or reconstruct MAF behavior.
- Keep implementer and reviewer roles sample-local. The SDK and runtime know only
  versioned descriptor kinds, not named model profiles.
- Document the local proxy operations used during dogfooding:
  `npx --yes openai-oauth@latest logs --follow` and
  `npx --yes openai-oauth@latest stop`.

Exit: the dogfood sample completes implementer capability behavior through OpenRouter
and reviewer structured-output behavior through the verified local Sol endpoint, with all
agent-loop and tool behavior still owned by MAF.

### 6. Package Runtime And Persistence

- Publish a macOS arm64 runtime artifact containing the generated Node module,
  Tandem/MAF assemblies, .NET model-transport dependencies, and RID-native SQLite
  assets.
- Make runtime and native dependency discovery package-relative and deterministic.
- Wire the public SQLite observer through host configuration.
- Expose a CLI-only process-exit helper for use after all work is awaited. The
  bridge is run-scoped/stateless and `node-api-dotnet` exposes no CoreCLR shutdown;
  do not claim flush or disposal behavior.

Exit: installation requires no local .NET project, DLL path, manual publish step,
or native-library configuration.

### 7. Dogfood One Real Sample

- Build a small but real function implementation pipeline whose state contains only
  requirements, exact source and rationale, verification evidence, and review facts.
  Compose implementer to deterministic verification, failed verification back to the
  implementer, passing verification to the reviewer, requested changes back to the
  implementer, acceptance to Done, and agent failures to Failed. Use a typed source
  capability that retains plain source data, typed reviewer output, persistence, and
  inspection; omit unrelated
  feature-demonstration participants and runtime mechanics.
- Keep executable-source verification in the ordinary verification operation. Its
  bounded child worker owns the Zod source-to-callable transform and returns only
  plain verification facts. Document that this containment is not a security sandbox;
  do not turn sample verification into product infrastructure.
- Verify install, author, typecheck, run, correct, persist, inspect, and terminate.
- Record bridge logs and diagnostics only when troubleshooting is enabled.

Exit: the complete journey is boring and application code contains no interop
knowledge.

### 8. Soak And Decide

- Exercise concurrent runs, long sessions, cancellation races, callback failures,
  provider failures, abandoned runs, persistence failures, and repeated startup.
- Monitor callback/reference retention and Node event-loop behavior.
- Pin Node API and test supported Node/.NET versions.
- Decide whether to continue, pause, or stop before adding more platforms.

Exit: an explicit go/no-go decision backed by dogfood and soak evidence.

## Verification

The experimental track must continuously run:

- TypeScript positive and negative compile tests;
- Node -> CoreCLR -> Tandem integration tests;
- normal Tandem `task check`;
- package-consumer tests for new Core APIs;
- macOS arm64 install/run tests from a clean fixture;
- SQLite accepted-value assertions; and
- concurrent/cancellation/lifecycle soak tests.

## Stop Criteria

Stop if:

- route choice, agent loops, correction, acceptance, or persistence logic moves
  into TS or the bridge;
- TypeScript sends model requests or depends on a vendor model SDK;
- Node API requires a maintained Tandem fork;
- callback references leak under soak;
- application authors must understand CoreCLR, DLLs, callback IDs, or graph JSON;
- inferred TS typing must be weakened to satisfy the bridge;
- installation cannot become package-only;
- every Tandem feature requires cross-cutting bridge changes; or
- the bridge grows beyond participant adapters and hosting mechanics.

## Deferred

- Stable API and compatibility guarantees;
- platforms other than macOS arm64;
- NativeAOT;
- Advanced Tandem parity;
- arbitrary provider coverage;
- generalized durability; and
- migration tooling.

## Immediate Next Actions

1. Extend the deterministic accepted capability/output protocol fixture with
   provider failures, correction failures, and timeouts.
2. Inject ledger terminalization failure and prove original run failures remain
   authoritative.
3. Exercise abandonment and a materially longer soak while monitoring callback
   and reference retention.
4. Validate the packed artifacts on the explicitly supported Node and .NET version
   matrix; registry publication and dependency resolution remain unproven.
