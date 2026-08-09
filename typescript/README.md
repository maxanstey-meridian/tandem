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
JSON-Schema-representable validation contracts. Async refinements are rejected, as
are coercion, defaults, transforms, and stripping whenever parsing changes the
boundary value. TypeScript and typed C# Tandem therefore observe the same state.
`ContractValidationError.problems` retains JSON-style paths and messages. The
registration protocol, callback IDs, serialized state, CLR types, graph JSON,
DLLs, and native loading remain private.

## Chat Clients

Each agent carries an opaque, versioned chat-client declaration. The implementer and
reviewer names below are sample-local roles, not SDK profiles; each sample agent is
given an OpenAI-compatible chat-client declaration. The runtime knows only the
versioned descriptor kind. Registration JSON contains an
endpoint, model, wire API, and optional API-key environment-variable name; it never
contains the resolved secret and TypeScript performs no model HTTP. The packaged
C# host validates the declaration and constructs `IChatClient` mechanically.

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
runtime allowlist into the RID package. Package checks enforce that exact list.
PDB/XML files, localized Roslyn resources, and the Node API source generator are
excluded; managed runtime dependencies are retained unless execution proves a
smaller allowlist.

The first-class sample in `sample/src/function-implementation.ts` implements and
reviews executable JavaScript `slugify(input)` source. State contains only application
facts: requirements, the latest implementation, deterministic verification evidence,
and the latest review. The implementer's typed `submit_implementation` capability
stores the exact source and rationale while clearing stale verification and review.
An ordinary async verification stage executes deterministic cases, failed verification
routes back to the implementer, passing evidence routes to the Sol reviewer, requested
changes route back, and acceptance reaches Done. Both agent failed selectors reach the
Failed terminal. The accepted review supplies the Done summary.

Generated source is evaluated only by sample support in a separate child process with
a cleared environment, fresh temporary working directory, bounded output, an overall
timeout, per-compilation and per-case VM timeouts, and string/Wasm code generation
disabled. The contract admits exactly one synchronous one-input function expression and
rejects Promise and non-string results. This is bounded containment for a trusted local
experiment, **not a security sandbox**, and is unsuitable for hostile code. The focused
tests prove these declared controls and ordinary access rejection, not adversarial
security.

Run it only with OpenRouter credentials and a running verified `openai-oauth`
endpoint:

```sh
OPENROUTER_API_KEY=... pnpm dogfood:function
```

The command fails rather than fabricating success if credentials or the reviewer
proxy are absent. It has completed successfully with the configured OpenRouter DS4
implementer and verified local Sol reviewer. SQLite inspection prints accepted
capability, structured-output, and state records. `TANDEM_LEDGER_PATH` optionally
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
Cancelled terminalization, callback failure, cancellation, concurrent runs,
repeated startup, explicit process exit, a bounded 25-run soak, and the live
OpenRouter plus `openai-oauth` dogfood. Long soak, abandonment,
persistence-failure injection, and the full provider-failure/correction matrix
remain deferred phase-8 work.
