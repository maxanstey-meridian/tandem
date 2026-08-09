# Tandem TypeScript Product Experiment

This macOS arm64 experiment packages a public `@tandem/sdk` facade, a
platform-neutral `@tandem/runtime` loader, and a private
`@tandem/runtime-darwin-arm64` host. Applications import only `@tandem/sdk` and
Zod. Tandem and MAF still execute the authored graph; TypeScript supplies typed
application semantics, not an agent loop or model transport.

## Prerequisites

- macOS arm64
- Node.js 22 or newer
- .NET 10 runtime (the package is framework-dependent)

Unsupported platform/RID and missing .NET errors identify those prerequisites.
Consumers do not build or publish .NET and runtime assets are loaded relative to
the installed runtime package.

## Packages

- `packages/sdk`: public stage, interaction, agent, capability, output, route,
  run, and accepted-value inspection facade.
- `packages/runtime`: platform-neutral loader. Its optional RID dependencies make
  adding another platform non-breaking for SDK consumers.
- `packages/runtime-darwin-arm64`: generated Node module, bridge assembly and
  runtimeconfig, Tandem/MAF/OpenAI-compatible managed dependencies, and exactly
  one macOS arm64 `libe_sqlite3.dylib` native asset.

Zod parses initial/final state, complete bridge results, accepted-value projections,
and every TypeScript callback input and output. Schemas are synchronous,
JSON-serializable, and validation-only: coercion, defaults, transforms, and stripping
are rejected whenever parsing changes a boundary value. Provider-facing schemas that
contain transforms are rejected before registration. Async refinements and transforms
remain unsupported.
`ContractValidationError.problems` retains JSON-style paths and messages. The
registration protocol, callback IDs, serialized state, CLR types, graph JSON,
DLLs, and native loading remain private.

Async stages receive `execute(state, { signal })` and interaction handlers receive
`handle(request, { signal })`. The signal is node-api-dotnet's maintained projection
of the active .NET `CancellationToken`; messages, predicates, interaction request/apply,
validation, capability summaries, and terminal summaries remain synchronous.

## Chat Clients

Each agent carries an opaque, versioned chat-client declaration. The implementer and
reviewer names below are sample-local roles, not SDK profiles; each sample agent is
given an OpenAI-compatible chat-client declaration. The runtime knows only the
versioned descriptor kind. Registration JSON contains an
endpoint, model, wire API, and optional API-key environment-variable name; it never
contains the resolved secret and TypeScript performs no model HTTP. The packaged
C# host validates the declaration and constructs `IChatClient` mechanically in its
OpenAI-compatible provider adapter. Model preflight uses active run cancellation.

| Sample role   | Endpoint                       | Wire API    | Model                             | Authentication       |
| ------------- | ------------------------------ | ----------- | --------------------------------- | -------------------- |
| `implementer` | `https://openrouter.ai/api/v1` | completions | `deepseek/deepseek-v4-flash-0731` | `OPENROUTER_API_KEY` |
| `reviewer`    | `http://127.0.0.1:10531/v1`    | responses   | `gpt-5.6-sol`, low reasoning      | none                 |

The supported experimental adapter has `kind: "openai-compatible"` and `version: 1`.
Endpoints must be absolute HTTP(S) URIs. Non-loopback endpoints require a valid
API-key environment-variable name; loopback endpoints may omit it. Wire API and
reasoning values are closed unions. Optional model discovery gets `/models` and
rejects unless the declared model is exposed. A future C# adapter may add another
kind/version without changing agent semantics.

An agent has one authored message, zero or more independently validated
capabilities, and optional structured output. Each capability seals its own typed
request contract while agents hold an opaque heterogeneous capability list. Those behaviors compile to one real
C# `AgentBuilder`; capability acceptance concludes the visit, otherwise structured
output correction and acceptance applies. Duplicate capability names are rejected.

For local proxy diagnostics and shutdown, use exactly:

```sh
npx --yes openai-oauth@latest logs --follow
npx --yes openai-oauth@latest stop
```

## Commands

```sh
pnpm install --frozen-lockfile
pnpm build
pnpm typecheck
pnpm test
pnpm pack-consumer
```

The clean packed-consumer gate installs all three locally packed Tandem tarballs into a
temporary directory outside the repository, executes a stage-to-terminal pipeline
through packaged CoreCLR/Tandem, and verifies its terminal SQLite row. It proves
the installed SDK-to-loader-to-RID topology and does no consumer-side .NET build
or publish. npm cannot rewrite dependencies inside a tarball to sibling local
tarballs, so the fixture must list all three as direct `file:` dependencies. This
is the strongest local proof without publishing package metadata to a registry;
only a registry install can prove that `@tandem/sdk` transitively resolves
`@tandem/runtime` and its platform-selected optional RID package.

The runtime build publishes to a private staging directory and copies an explicit
runtime allowlist into the RID package. Package checks enforce that exact list. The
bridge eagerly loads that managed closure before MAF starts background work because
node-api-dotnet's lazy assembly resolver requires an active JavaScript scope; native
SQLite resolution remains exact and lazy.
PDB/XML files, localized Roslyn resources, and the Node API source generator are
excluded; managed runtime dependencies are retained unless execution proves a
smaller allowlist.

The first-class sample under `sample/src` implements and reviews executable JavaScript
`slugify(input)` source. Its authoring structure follows Tandem's semantic ownership:

```text
sample/src/
|-- agents/
|   |-- implementer.ts
|   `-- reviewer.ts
|-- capabilities/submitImplementation.ts
|-- infrastructure/
|   |-- assess-implementation.ts
|   `-- assess-implementation-worker.mjs
|-- stages/verification.ts
|-- state.ts
|-- pipeline.ts
`-- run.ts
```

`state.ts` owns the cohesive application fact contracts and pure implementation,
verification, and review transitions; recording a new implementation clears stale
verification and review. Each agent file owns its instructions, message, validation,
and declaration. The typed `submit_implementation` capability owns its schema,
summary, and transition. The verification stage delegates executable-source mechanics
to a bounded child-process adapter. Inside that worker, one Zod transform evaluates
the source to a callable `Slugify`; the worker invokes it and returns only plain
verification facts. The child has an empty environment, isolated working directory,
output limit, VM compilation timeout, and hard process timeout. This is bounded
containment, not a security sandbox. `pipeline.ts` is the lexical composition root:
it constructs every participant and visibly declares every route while accepting the
implementer and reviewer clients. `run.ts` supplies the concrete DS4/Sol declarations
and process-owned run mechanics. Both agent failures reach Failed, and an accepted
review supplies the Done summary. The capability schema accepts source as a plain
string; executable values never enter pipeline state, capability callbacks, or the
bridge. Run this demo only where bounded local execution of model-generated code is
acceptable.

Run it only with OpenRouter credentials and a running verified `openai-oauth`
endpoint:

```sh
OPENROUTER_API_KEY=... pnpm dogfood:function
```

The command fails rather than fabricating success if credentials or the reviewer
proxy are absent. It has completed successfully with the configured OpenRouter DS4
implementer and verified local Sol reviewer. SQLite inspection prints accepted
capability and structured-output wire values plus accepted state records. Provider
wire payloads remain the durable evidence; Tandem does not persist adapter-local
transformed values. `TANDEM_LEDGER_PATH` optionally
selects the run ledger; otherwise each invocation uses a unique ledger. Ledger
location is a host/run option, not authored graph identity. The sample applies an
`AbortSignal` timeout, always calls `closeCli`, exits nonzero on error, and prints the
terminal result and accepted semantic values returned by `inspectAccepted`.

## Lifecycle

The bridge terminalizes SQLite runs as `Ready`, `Failed`, `Faulted`, or
`Cancelled`. Terminalization is best-effort while an original run failure is being
propagated, so a ledger failure cannot replace that original error.
`node-api-dotnet` 0.9.21 provides no host shutdown API, and this bridge owns no
cross-run resource to close. Long-running hosts need no lifecycle helper. CLI
programs may import `closeCli` from `@tandem/sdk/cli` and call it only after all run
and inspection promises have been awaited; it directly invokes `process.exit` and
does not flush or dispose CoreCLR.

## Automated Evidence

The gates cover facade positive/negative compilation, runtime participant identity,
registration rejection, reviewer model discovery and request progress, capability
message transport, the deterministic function implementation/verification/review loop,
sample-local verifier controls, package contents,
local-tarball package-relative execution,
the exact SQLite native asset, accepted-value inspection, Ready/Failed/Faulted/
Cancelled terminalization, propagated callback failure, cancellation observed inside
JavaScript operations before post-cancel mutation, interaction chains, concurrent runs,
repeated runs in one loaded host, explicit process exit, a bounded 25-run soak, and the live
OpenRouter plus `openai-oauth` dogfood. Long soak, abandonment,
persistence-failure injection, and the full provider-failure/correction matrix
remain deferred phase-8 work. Process-exit fixtures prove the documented CLI helper,
not natural CoreCLR shutdown or reference reclamation.
