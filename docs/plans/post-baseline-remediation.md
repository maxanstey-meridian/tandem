# Post-baseline review and remediation plan

Status: implemented and verified; all R1–R8 and M1–M6 items are complete. Changes remain uncommitted.
Created: 2026-09-21.

## Purpose and scope

Bring the changes after the last agreed baseline to a state that can be approved as a PR: correct behaviour, honest public contracts, complete build/release wiring, and small implementations consistent with Tandem and Meridian.

- Baseline: `3b707ed6477170e52ac92ea650adb85289ff16f3` (excluded from the reviewed changes).
- Reviewed HEAD: `25707197c24302d618952c6aceb88560dbd6fe42`.
- Review range: 19 commits, 199 files, approximately 5,016 insertions and 14,673 deletions.
- Scope: the resulting Tandem tree, including runtime, Advanced tools, ledger, provider adapters, bridge, tests, packages, release scripts, and documentation.
- The TypeScript SDK and Studio were moved out of this tree. Their external implementations were not reviewed. Coordinate contract changes with `tandem-ts`; do not claim its tests passed without running them.
- This document authorizes no publishing, tagging, or changes to external repositories. It is the work specification for subsequent implementation.

Use stable IDs below in commits, test evidence, and progress updates. Check an item only when its desired outcome and validation are satisfied. Record an explicit disposition for any item that evidence invalidates or makes unnecessary.

## Governing constraints

Read `README.md`, `CONTRIBUTING.md`, and applicable `AGENTS.md` before implementing. Apply the Meridian skill at `/Users/max/.config/opencode/skills/meridian/SKILL.md`, especially its coding philosophy, invariant/concurrency, and testing references.

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

- MAF owns live workflow execution, sessions, model loops, and tool dispatch. Do not create another scheduler, retrying agent loop, or application coordinator.
- Runs remain process-owned. A ledger retrieval fix must not introduce generalized durability or resume machinery.
- Keep runtime IDs, offsets, parsing modes, and execution evidence out of application state.
- Raw parsing and execution-policy hooks belong in Advanced/private implementation, as explicitly stated in CONTRIBUTING.
- A capability acceptance still concludes its visit. Persistence/acceptance completes before state application or routing.
- Prefer a maintained framework or SDK for commodity behaviour. Preserve small local policy where its requirement is concrete; do not replace a few lines with an unnecessary dependency.
- Keep declared input failures distinguishable from unexpected runtime faults. Do not infer permissions from tool-name prefixes.
- Public API changes require deliberate updates to owning `ExportedApi.txt` and `PublicApiMembers.txt`, plus appropriate packed-consumer proof.
- Before modifying an existing symbol, run GitNexus upstream impact analysis and report direct callers, affected processes, and risk. Warn before HIGH/CRITICAL changes. Before a commit run `detect_changes`; review the overall branch against `main` as required by AGENTS, and retain the user-specified baseline as the review scope.
- The previous GitNexus index was stale and native parser workers failed during refresh. Resolve that tooling prerequisite before symbol edits; do not present a stale graph as current impact evidence. Writing this Markdown plan does not modify a symbol.
- Preserve the user's existing untracked files and unrelated work. Recheck HEAD/worktree before implementation and account for changes since the reviewed HEAD.

## Evidence from the review

### Confirmed by execution

- Bridge build against its declared dependencies fails with CS0246 for `IAgentRawOutputDefinition<,>`.
- An actual MAF pipeline using a capturing chat client receives no raw-output definition instructions.
- The same pipeline receives a JSON-only correction request after a raw parser rejects a response.
- Paging `short\n` followed by a 180,000-character line and another short line reconstructed the long line as 180,069 characters.
- A command that captured 9,000 stdout characters passed 8,000 to acceptance with `Truncated == false`.
- `task typescript:test` fails with `ERR_PNPM_NO_IMPORTER_MANIFEST_FOUND` after the SDK directory removal.
- Bridge-test restore reports the missing project resolved from `../../src/Tandem.OpenAICompatible/...`.
- Core suite: 422/422 passed. Packed-consumer suite: 1/1 passed. Other passing suites included packets and external consumers.
- The broader solution run had one example-host assembly-loading failure for `Microsoft.Extensions.AI.OpenAI`; the dependency path was unchanged. This was not established as an introduced regression.
- CSharpier reported three changed files: `AgentBlock.cs`, `RegisteredParticipants.cs`, and `RawStructuredOutputTests.cs`.
- Plumb returned no findings. That does not invalidate behavioural or architectural review findings.

### Evidence limits

Initial checks on the workspace volume encountered filesystem-sharing failures. The reproducible builds and behavioural probes were run from an archived HEAD under `/tmp/tandem-pr-review` with a separate `/tmp/tandem-review-probe` application. Those paths and logs are temporary conveniences, not permanent acceptance evidence. Recreate meaningful regression tests in the repository during implementation.

A suspected failure where an invalid argument count on an arbitrarily named command would abort the run was tested and did **not** reproduce: MAF returned feedback and the run continued. Do not report that hypothesis as a confirmed bug. Error classification still warrants the bounded audit in M5.

## Confirmed remediation work

### R1 — P1: Make the bridge and release bundle consume compatible Tandem assemblies

- [x] Implement and verify.

**Issue:** `bridge/Tandem.NodeApiSpike.Bridge.csproj` pins `TandemVersion` to 0.1.0 while `RegisteredParticipants.cs` references a new interface absent from that package. `.github/workflows/release.yml` extracts a tag version but does not pass it to the bridge build. Fixing only the missing interface leaves future bundles able to carry old runtime assemblies under a new filename.

**Desired outcome:** A clean checkout builds the bridge; a release bundle contains the runtime implementation associated with its release source/version; no dependence on unpublished APIs or an unrelated cached package.

**Implementation direction:**

1. Establish one explicit dependency/version policy for the bridge now that its source lives in this repository.
2. Preferred same-repository default: build bridge and runtime from the same checkout using project references; prove the public package boundary separately through packed consumers. First inspect the commit that deliberately switched to published packages and record any concrete constraint against this choice.
3. If consuming packages is a real requirement, pack this checkout into a temporary local feed and build/test the bridge against that exact version. Use existing package-test infrastructure where possible. Do not require a NuGet publication merely to test a PR.
4. Wire release version consistently into pack/build/publish. Check runtime assembly/package metadata, not just the tarball name.
5. Keep the currently supported platform scope unless expansion is separately required. Validate the allowlist in `scripts/runtime-assets.mjs` against actual publish output.
6. Coordinate release stages so a bridge cannot silently consume stale packages. Do not add a retry/poll loop waiting for public NuGet indexing if a local build/feed solves the problem.

**Acceptance:** Clean restore/build and bridge tests succeed without local custom feeds; a packed consumer exercises the new output contract; a staged macOS ARM64 bundle loads in Node and executes a minimal pipeline; bundled dependency versions correspond to the chosen release policy. No publication is required to prove these outcomes.

**Files:** bridge project, release/publish workflows, runtime staging scripts, package-consumer tests, bridge tests.

### R2 — P2: Make truncated acceptance evidence honest

- [x] Implement and verify.

**Issue:** `AgentBlock.cs` replaces process stdout/stderr with 8,000-character tails for acceptance observations, but copies the original capture-truncation flag unchanged. A policy cannot tell that earlier output has disappeared.

**Desired outcome:** Every acceptance policy can distinguish complete evidence from a preview. Full captured diagnostics remain retrievable through the authorized ledger path, without granting new ledger permissions.

**Implementation direction:** Preserve bounded evidence. At minimum, set the existing evidence truncation flag when either capture or preview lost content; document its meaning. Add separate public metadata only if consumers genuinely need to distinguish the two. Keep the tool response's `captureTruncated` and `previewTruncated` meanings accurate. Do not solve the bug by handing every policy an unbounded payload.

**Acceptance:** A real command producing 9,000 characters gives acceptance a bounded value marked truncated; short output remains complete; hard capture truncation stays marked; surrogate boundaries are preserved; ledger retrieval reconstructs all captured text when enabled. Exercise output acceptance and capability acceptance through their common runtime boundary. An early failure marker must never disappear while the evidence claims completeness.

**Files:** `src/Tandem/Infrastructure/Blocks/AgentBlock.cs`, Advanced evidence mapping/contracts if needed, command-observation and diagnostic tests.

### R3 — P2: Correct long-line continuation accounting

- [x] Implement and verify.

**Issue:** `BoundedLinePageReader.ReadAsync` initializes `consumedInLine` to `characterOffset` after already buffering a suffix in `pending`. That suffix is omitted from the next returned offset, causing repeated characters on later pages.

**Desired outcome:** Following returned continuations reconstructs every line exactly once, with bounded memory and valid Unicode boundaries.

**Implementation direction:** Account for all characters already read within the current line. Keep continuation stateless and explicit. Do not introduce opaque cursor stores or snapshot tracking. Review the surrounding end-of-line/EOF logic while making the local correction.

**Acceptance:** Reconstruct a long line following a short line; multiple consecutive long lines; a long first line; Unicode pairs at page boundaries; CRLF/LF; EOF with and without a newline; changed page sizes; UTF-8 and supported BOM encodings. Use a compact parameterized matrix focused on meaningful outcomes. Verify cancellation and the existing bounded-read guarantee. The original 180,000-character case must remain exactly 180,000 characters.

**Files:** bounded line/text readers, relevant line-reader tests. See M4 before expanding reader machinery.

### R4 — P2: Deliver raw-output instructions to the model

- [x] Implement with M1 and verify.

**Issue:** `AgentBuilder.WithRawOutput` constructs an output descriptor without `output.Instructions`, so the public property and required bridge field are ignored.

**Desired outcome:** Authored raw-output instructions appear in the effective model request while JSON-schema response format and JSON-schema prompt additions remain absent.

**Acceptance:** A pipeline-level capturing client observes the authored instructions on the first turn and appropriate continuation/correction turns. Raw mode sends no JSON response format. JSON mode still sends its configured schema. Switching builder configuration before Build must not retain an obsolete response format. Do not stop at testing the parser helper.

**Files:** output authoring/descriptor, `AgentBlock.cs` prompt assembly, raw-output tests, bridge output construction. Final public placement is determined by M1.

### R5 — P2: Make correction prompts respect the output contract

- [x] Implement with M1 and verify.

**Issue:** Raw parse or validation rejection calls `CorrectionPrompt`, whose last instruction always requests a JSON object.

**Desired outcome:** A raw-text parser can recover in the same agent session using its authored format; JSON output retains its schema-specific correction. Recovery remains bounded and fails closed.

**Implementation direction:** Represent the distinction narrowly in private output configuration. Prefer one correction path with format-appropriate content over a second output execution loop. Do not use JSON-schema absence alone if existing Advanced outputs also omit schemas with different semantics.

**Acceptance:** A client returns invalid raw text and then valid raw text only when instructed appropriately; the run accepts the second value and applies state exactly once. Repeated invalid output fails with raw response/problems. JSON correction remains unchanged in meaning. Accepted capability calls bypass structured-output correction.

**Audit note:** CONTRIBUTING says one corrective response; the reviewed runtime constant is `StructuredOutputCorrectionLimit = 2`. Establish whether this mismatch predates the range and test the effective call count. Record a separate disposition rather than silently changing an unrelated retry budget.

### R6 — P2: Preserve authored output identity through the raw bridge path

- [x] Implement with M1 and verify.

**Issue:** The JSON bridge path forwards `outputContract.ValueType`; the raw path does not. Since its CLR output is `JsonElement`, accepted records fall back to `System.Text.Json.JsonElement` instead of the authored semantic identity.

**Desired outcome:** Accepted raw outputs preserve their application-owned value type through observations, persistence, and `InspectAcceptedAsync`.

**Implementation direction:** Carry the existing bridge identity through the same internal acceptance metadata used by JSON output. Keep transport metadata out of TState. Do not add a second ledger record or infer types by inspecting JSON content.

**Acceptance:** Persist a raw bridge output named, for example, `review-decision`; inspect accepted records after reopening the ledger and assert that exact identity and payload. Two distinct raw output types must remain distinguishable. Native typed C# outputs keep their CLR identity. Validation still happens before acceptance and state application.

### R7 — P2: Finish the root check/task migration

- [x] Implement and verify.

**Issue:** `Taskfile.yml` still invokes `pnpm test` in the removed `typescript` directory, including from `task check`.

**Desired outcome:** The documented root check is executable from a clean clone and covers everything this repository now owns.

**Implementation direction:** Remove the obsolete SDK test invocation and replace it with explicit bridge validation where appropriate. Keep build/test/format/package tasks boring and discoverable. Update documentation describing check responsibilities. Do not silently drop bridge checks along with TypeScript tests.

**Acceptance:** `task check` completes on a clean checkout with documented prerequisites; there is no dependence on a sibling checkout. Task composition explicitly includes bridge tests and preserves packed-consumer/analyzer-delivery checks.

### R8 — P2: Repair bridge-test paths and include the tests in routine validation

- [x] Implement and verify.

**Issue:** The moved bridge-test project still references `../../src/Tandem.OpenAICompatible/...`, resolving outside the repository. The main solution omits bridge and bridge tests.

**Desired outcome:** Bridge tests resolve only legitimate dependencies and run in the normal PR validation path.

**Implementation direction:** Repair/remove the stale reference according to the dependency policy chosen in R1. Avoid mixing local and packaged versions of the same assembly accidentally. Add the projects to the solution, or use an explicit root task invoked by CI if an actual platform constraint requires separation. Prefer solution inclusion when it works.

**Acceptance:** Clean restore has no missing-project messages; bridge tests execute during documented checks and CI; changes using a nonexistent Tandem API fail the check. Verify the moved test project does not rely on old working-directory assumptions or deleted fixtures.

## Meridian cleanup work

### M1 — Put raw parsing below the Core seam and consolidate output validation

- [x] Implement and verify with R4–R6.

**Issue:** `IAgentRawOutputDefinition` and `AgentBuilder.WithRawOutput` put a raw parsing hook in Core, contrary to the explicit boundary in CONTRIBUTING. `ValidateRaw` copies the existing intrinsic/contextual validation and accepted-payload construction. Advanced already exposes structured-output parsing hooks.

**Desired outcome:** Ordinary authors retain typed output, validation, and state mapping. Authors deliberately supplying a parser use an Advanced extension. Both routes share the same acceptance invariants and validation implementation.

**Steps:**

1. Inspect the existing Advanced `WithOutput`/`StructuredOutputParser` surface and the Core typed-output path together.
2. Prefer extending/reusing the existing Advanced seam over adding a second family of almost-identical contracts. If a typed Advanced raw definition is necessary, document the semantic value it adds.
3. Keep one internal candidate-validation/acceptance path, preserving intrinsic validation before contextual validation and one accepted transition.
4. Preserve authored instructions, correction semantics, bridge value identity, cancellation, and exception distinctions.
5. Update public API inventories and packed-consumer examples deliberately. Establish whether any affected API was actually published before deciding compatibility treatment; do not retain an accidental Core seam solely because it exists on HEAD.
6. Add concise native and bridge-facing documentation. Coordinate the external TypeScript adapter if its wire contract must change.

**Acceptance:** Core no longer advertises raw parsing as ordinary authoring; an Advanced packed consumer can configure it; R4–R6 pass; existing typed JSON and capability acceptance behaviour passes unchanged. No parallel agent loop or duplicate validator pipeline is introduced.

### M2 — Replace the handwritten FluentValidation adapter

- [x] Implement and verify.

**Issue:** `RegisteredParticipants.DelegateValidator` manually implements the generic and nongeneric validator interface, async wrappers, descriptor creation, and type checks.

**Desired outcome:** The bridge only translates callback validation problems into FluentValidation failures; FluentValidation owns validator mechanics.

**Direction:** Use `InlineValidator<JsonElement>` with an appropriate custom rule, or an existing maintained facility already in use. Preserve property paths and messages. Dispose the temporary `JsonDocument` used by raw parse adaptation after cloning its root. Audit the broad parse exception wrapper: expected parse rejection can become corrective feedback, but cancellation must remain cancellation and programmer/transport faults must not all become model mistakes.

**Acceptance:** Valid and invalid contextual results, multiple problems, and cancellation behave correctly through a real pipeline/bridge boundary. The handwritten interface implementation is gone. Do not add an abstraction around `InlineValidator` merely to replace one wrapper with another.

### M3 — Give workspace command arguments one honest meaning and one owner

- [x] Trace consumers, decide the contract, implement, and verify.

**Issue:** `AgentCommand.Arguments` stores and validates string contents, but runtime execution/schema generation uses only whether the list is empty. The strings are neither fixed appended arguments nor an enforced allowlist nor useful per-argument model guidance. `PacketCommand` wraps `AgentCommand`, and `PacketValidator` repeats its admission rules; no production consumer was found in this repository.

**Desired outcome:** Command authors can tell exactly which text is fixed and which values a model may supply. There is one authoritative admission policy and no unused public packet abstraction.

**Direction:**

1. Trace actual consumers, including known bridge contracts and any available Cadence/tandem-ts consumers, before altering the public representation. External changes require their own scoped work.
2. Default to preserving demonstrated behaviour: fixed command text plus explicitly permitted bounded model arguments. If the current string list is only an enablement switch, express that honestly; do not silently turn it into an allowlist or append it as fixed arguments.
3. If consumers actually need fixed arguments or allowed options, model that specific meaning and enforce it. Shell quoting prevents interpolation but does not make arbitrary program flags safe; document actual authority.
4. Remove unused PacketCommand/PacketValidator, or place a genuinely needed transport DTO at its owner and delegate admission to AgentCommand. Do not duplicate validation rules across DTO, bridge, and runtime.
5. Use typed models for known response/configuration fields. Keep genuinely dynamic JSON only at the transport boundary.

**Acceptance:** Execution tests prove the declared argument meaning, empty/default behaviour, count/length constraints, quoting, and invalid input feedback. A configuration field cannot accept meaningful-looking values that have no effect. API inventories, bridge contract, documentation, and compatibility disposition match the chosen semantics.

### M4 — Keep paging/search implementation proportional to its requirements

- [x] Complete the bounded audit and record each disposition.

**Concern, not a blanket defect:** The changes add custom line fragmentation, BOM handling, traversal, globs, exclusions, and paging response shapes. Some code is justified by bounded-memory reads, Unicode-safe continuation, and workspace authority; some may duplicate framework behaviour.

**Desired outcome:** The smallest maintained implementation that preserves exact paging, bounded memory, cancellation, and path restrictions.

**Direction:** Compare each responsibility against existing StreamReader decoding/BOM support, the already referenced filesystem-globbing facilities, MAF tools, and existing process/read abstractions. Keep a small local fragment reader if standard line reads cannot meet the memory requirement. Replace commodity portions only where the maintained alternative demonstrably fits. Record concrete constraints for retained custom code; do not add ripgrep deployment or another parser just for stylistic consistency.

**Targeted audit:** Offset past end; surrogate boundaries; malformed text; regex timeout; excluded directories and explicit path overrides; symlink/.git restrictions; mutable-tree continuation; bounded output and cancellation. Verify prefix/glob semantics with examples instead of broad refactoring. Preserve the documented no-snapshot guarantee. If a new bug is found, give it a separate R ID and reproduction.

### M5 — Remove ad hoc tool-input protocol semantics where the framework already owns them

- [x] Complete the bounded audit and implement justified cleanup.

**Concern:** `ToolInputValidation` implements part of JSON Schema interpretation and classifies exceptions by name prefixes. This is correctness-sensitive commodity behaviour, but replacement requires understanding MAF's actual binding/error handling.

**Desired outcome:** Malformed model calls produce useful bounded feedback; unexpected faults remain observable; changing a tool's name does not change its authority or intended validation semantics.

**Direction:** Establish MAF/AIFunction argument binding guarantees first. Use existing maintained schema validation only where genuinely required. Keep Tandem's pagination/path-policy errors explicit and local. Prefer typed expected exceptions or registration-owned metadata over prefix guessing. Do not broaden catches to hide arbitrary exceptions. The prior arbitrary-command abort hypothesis was disproved; any replacement must be justified by actual semantics, not that hypothesis.

**Acceptance:** Focused real-tool tests cover missing/unknown/type-invalid arguments, supported schema constructs actually used by Tandem, pagination recovery, expected path failures, unexpected I/O faults, and cancellation. Keep existing capability validation authoritative. Avoid a new general schema engine.

### M6 — Finish documentation, formatting, and public-contract cleanup

- [x] Implement and verify.

**Desired outcome:** A clean checkout has accurate instructions, no dangling migration references, consistent public inventories, and passing format checks.

**Steps:**

- Format the three known failing files and any files changed by remediation.
- Audit references to removed `typescript/` paths, deleted example halves, moved scripts, old provider class names, obsolete package versions, and claims of matching local C#/TS examples. Update only where the migration made them inaccurate; retain intentional history.
- Correct read-file continuation guidance: resume using both the returned line and character offset where supplied. The Advanced README currently tells readers to omit startLine mid-line, which can read the wrong line.
- Document ledger tool opt-in, preview/capture truncation, argument authority, raw-output placement, and release/check commands consistently.
- Update exported types/member inventories and package README examples with the final API, not intermediate designs.
- Run Plumb once after meaningful changes; fix errors and either address warnings or record the specific justified exception. Do not loop on an unchanged tree.

**Acceptance:** CSharpier/analyzer checks pass; removed-path references are either gone or explicitly historical; documented commands work; API tests and packed consumers match the shipped surface.

## Preserve and verify other changed behaviour

These are regression coverage obligations, not additional confirmed bugs. Avoid rewriting working features without evidence.

- **Parallel caps:** The limit remains per group invocation; slots are released as branches finish; queued branches cancel; failed branches do not cause a merge of success state; MAF still owns graph execution. Preserve existing concurrency tests and inspect lease cleanup if changes touch it.
- **Acceptance/observation serialization:** Keep ambient transaction ownership, no cross-branch writes entering an acceptance transaction, no observer reentrancy deadlock, and accepted state application order. A fake unit-of-work test alone cannot prove SQLite transaction behaviour; use the real ledger boundary when changing this mechanism.
- **Ledger startup cancellation:** The run is consistently terminalized even when cancellation is already requested. Preserve the original failure if bookkeeping also fails. Do not create indefinitely uncancellable application work to solve a startup bookkeeping race.
- **Ledger retrieval:** Same-run and readable-record restrictions hold; ledger access stays opt-in; process diagnostics map to the correct invocation. Audit lookup cost only against realistic evidence, not hypothetical scale.
- **Provider deadlines/retries:** Total/idle deadline semantics remain per attempt; partial failed responses are discarded where promised; caller cancellation does not retry; configured attempt budgets account for SDK retries. Do not add another retry layer.
- **Context budgets:** Harness receives configured context/output limits and compaction policy; usage observations report the same policy. Budget settings remain execution configuration, not lifecycle facts.
- **Generator/package delivery:** Global-namespace stage support remains valid; analyzers ship under the established path; optional dependencies stay isolated.
- **TypeScript split:** Verify bridge ABI/contract compatibility with the external SDK before a release. Record what was tested externally and what remains a dependency; do not recreate the removed SDK in this repository.

## Execution order and change boundaries

1. **Preflight:** Check HEAD/worktree, read instructions, restore current GitNexus analysis, inventory real consumers, and record impact before each symbol edit. Decide R1's dependency policy and M1's output seam before touching public APIs.
2. **Restore build accountability:** R1, R7, R8. Make local bridge compilation and tests part of the normal check path. A temporary local package feed is acceptable verification infrastructure; a public release is not a prerequisite.
3. **Repair evidence and paging:** R2 and R3, with their narrow regression tests. Keep unrelated search rewrites out of this change.
4. **Complete raw output coherently:** M1, R4, R5, R6, M2. Land one internally consistent API/bridge/validation change with public-boundary proof; avoid first patching the accidental Core API and then proliferating compatibility shims around it.
5. **Resolve command ownership:** M3, after consumer tracing. Coordinate external API dependencies explicitly.
6. **Bounded machinery audit:** M4 and M5. Implement only earned reductions or reproduced fixes; record retained mechanisms and why.
7. **Finish and validate:** M6 plus the regression obligations above, clean packed/runtime smoke checks, and a final baseline review.

Use cohesive commits aligned with these boundaries if commits are requested. Do not mix mass formatting or unrelated changes into behavioural fixes. Before committing, run the mandated GitNexus change-scope check.

## Validation strategy

Use pure tests for transforms and paging, real local files/processes for workspace tools, real SQLite for ledger behaviour, real MAF in-process execution for agents/routes/acceptance, and packed consumers for public APIs. A scripted chat client is appropriate at the model boundary; do not mock away the runtime under test.

Required targeted proofs:

- [x] R2 evidence is bounded and accurately marked, with complete authorized retrieval.
- [x] R3 page reconstruction is exact in the representative matrix.
- [x] R4/R5 raw prompts, validation, recovery, and state application work through MAF.
- [x] R6 identity survives bridge acceptance and ledger inspection.
- [x] M1/M2 one validation path handles intrinsic/contextual rejection and cancellation correctly.
- [x] M3 actual command execution matches the final declared argument contract.
- [x] Existing parallel, ledger atomicity/cancellation, and provider retry/deadline tests pass.
- [x] Bridge build/test and Node runtime smoke run against the intended dependencies.
- [x] Packed C# consumers prove the final Advanced output API and analyzer delivery.

Final commands, after task wiring is repaired:

```sh
dotnet tool restore
task check
# Run explicitly if not already included by task check:
dotnet test bridge-tests/Tandem.NodeApiSpike.Bridge.Tests.csproj
# Run once after changes, with documented dispositions for warnings:
~/Sites/plumb/plumb . --json
```

Also perform release staging/smoke validation appropriate to R1, without publishing. Do not repeatedly run the full suite once it passes unless new changes or unresolved failures warrant it.

Investigate the example-host assembly-loading failure using a clean restore and, if necessary, a baseline comparison. Either fix a demonstrated in-scope regression or record a precise pre-existing/environmental limitation. Do not label the entire validation green while an unexplained failure remains.

## Definition of done

- [x] Every R item has a fix and meaningful evidence, or a documented evidence-based disposition.
- [x] Every M item is implemented or has a concrete justified retention/deferral; no vague "future cleanup" for confirmed defects.
- [x] No new orchestration engine, generic durability, ambient lifecycle state, or duplicate acceptance implementation.
- [x] Public API changes are intentional, correctly placed, documented, and exercised as packed consumers.
- [x] Build/test/release wiring works from a clean checkout without hidden local dependencies.
- [x] Format/analyzer/Plumb results are recorded; any remaining failure is explained accurately.
- [x] Cross-repository compatibility work is completed when in scope or explicitly listed as a release dependency.
- [x] Final diff reviewed against the user baseline and the required default-branch comparison; unrelated work preserved.
- [x] Progress log below contains the final validation evidence and remaining limitations.

## Progress log

### 2026-09-21 — Plan created

- Persisted the eight review findings, six Meridian cleanup workstreams, implementation sequence, and acceptance checks.
- Distinguished executed reproductions from code-traced findings and bounded audit concerns.
- No implementation changes, commits, tags, or publications performed as part of creating this document.

For each implementation update append: IDs addressed; decision/change; tests and results; remaining risk or external dependency. Update the checkboxes and final design text as decisions settle so this file remains the executable work specification rather than a history of abandoned approaches.

### 2026-09-21 — Implementation and verification

**R1, R7, R8:** The bridge now uses same-checkout project references, is in the solution with its
84 tests, and ships assemblies built from the release tag with its MSBuild version. The removed
TypeScript task is gone. Packed consumers remain the independent package-delivery proof. No local
feed, public publishing dependency, version polling, or alternate bridge build mode was added.

**R2, R3:** Acceptance evidence marks either hard capture truncation or a shortened stdout/stderr
preview. Paging now includes the already buffered suffix in the continuation offset. New real
pipeline/process tests cover 7,999/8,000/9,000 characters; reconstruction covers preceding empty,
short, and longer lines, changing page sizes, Unicode, UTF-8 and UTF-16. Existing ledger diagnostic
retrieval and hard-capture tests remain green.

**R4–R6, M1, M2:** Raw definitions and the extension now live in Advanced. A typed definition earns
its place by retaining typed validation and accepted observations, unlike the lower-level parser
returning runtime outcomes. Core and raw candidates use one validation routine. Raw prompts retain
instructions and format-neutral correction; adapter value identity reaches both typed observations
and the ledger. The bridge uses InlineValidator and disposes parsed JSON. Cancellation/unexpected
parser exceptions are no longer broadly wrapped. Bridge integration tests exercise two semantic
identities, multiple contextual problems, correction, single state application, SQLite, and
InspectAcceptedAsync. The packed Advanced consumer executes raw output. Ordinary typed JSON,
capabilities, sessions, parallelism, cancellation, deadlines and ledger tests pass.

**M3:** Cadence consumes the argument list; its packet DTO/validator are application-owned and do
not use Tandem's PacketCommand wrapper. The TypeScript bridge likewise passes the list directly.
The compatible settled meaning is **example model arguments**: a nonempty list enables bounded
optional arguments and appears as an example array in the schema. It is neither fixed argv nor an
allowlist. Execution and quoting stay unchanged. Schema construction now uses known typed fields,
not dictionaries. The duplicate PacketCommand/PacketValidator are removed; admission remains in
AgentCommand. Those two types exist in published 0.1.0, so their removal is explicitly documented as
a breaking Advanced API change. No compatibility wrapper was added. Raw output is absent from the
published 0.1.0 Core package. API inventories reflect these deliberate changes.

**M4 — retained mechanisms:** StreamReader already owns decoding. The small BOM selection remains
because all supported UTF encodings must reject malformed bytes; ordinary BOM autodetection can
replace the strict decoder with a replacement-fallback encoding. Fragment reads are needed to bound
memory for oversized lines and preserve surrogate boundaries; ReadLine would allocate an entire
line. Search traversal retains early stopping, deterministic continuation, cancellation, directory
pruning, explicit-prefix overrides and symlink/.git exclusions. The small glob translation supports
`?`, `*`, `**` and slashless filename matching; the referenced filesystem matcher documents `*`/`**`
but is not a drop-in for that contract. No extra process or dependency was justified. Existing
paging/search tests cover malformed data, offsets, regex failures, exclusions, path escape, links,
Unicode and oversized matches. The reproduced offset bug is fixed as R3; no broader rewrite.

**M5:** Removed the partial JSON type/required-field interpreter. Real AIFunction binding tests
prove that the framework rejects missing/type-invalid values before execution. Tandem retains
unknown-argument rejection and pagination/path-policy feedback. Expected filesystem/search errors
use registered tool semantics, never name prefixes; command input errors use ToolInputException.
General I/O faults, programmer faults and cancellation stay faults. Existing real-pipeline recovery
tests caught and verified the distinction between binding errors and filesystem failures.

**M6:** Corrected continuation guidance, package versions and the removed SDK link; documented raw
placement, command authority, packet API migration, bridge checks and release staging. Formatting
and API manifests are updated. No external repository source was edited.

**Validation:** `task check` passed on a canonical local copy of the worktree: 630 tests
(439 Core/infrastructure, 84 bridge, 91 terminal, 8 external consumers, 7 packets, 1 packed-consumer
suite); format/analyzers passed; build had zero warnings/errors. After the final schema-only cleanup,
26 relevant execution/recovery tests passed again. Plumb returned `[]`. ARM64 publish and allowlist
staging passed without publication. The earlier example-host assembly failure disappears with a
clean restore using canonical `/private/tmp` paths; mixing `/tmp` and `/private/tmp` produced broken
NuGet project-reference graphs. The workspace volume also rejects GitNexus database locks, so impact
analysis used a fresh local index. The required main/worktree scope commands were run, but their
workspace index is stale; they report CRITICAL broad branch impact and cannot certify this diff.

Existing/concurrent SQLite store/tests and bridge scheduling edits were preserved. The passing
combined-tree checks include them, but this remediation does not claim ownership of those changes.
No commit, tag, release or publication has been made.

**Native smoke:** The existing built tandem-ts SDK successfully loaded the staged macOS ARM64
bundle, ran raw output through contextual correction, applied state once, and read the preserved
`review.output` identity/payload through inspectAccepted. This used local HTTP/SQLite and pinned
node-api-dotnet 0.9.26 / Zod 4.3.6 in a temporary directory, without altering the sibling checkout.
A source SDK typecheck was abandoned because dependency reads on the workspace volume stalled;
this proof used its existing distribution. Native C# packed-consumer and bridge contracts are green.
The final command-schema simplification was separately covered by 26 passing execution/recovery
tests. An additional stderr-only truncation case passed through the real pipeline as well.

**Final check:** After the last schema/test changes, `task check` passed again: **631 tests**, zero
failures, zero build warnings/errors, and passing CSharpier/analyzer checks. Plumb returned `[]`.
The edited implementation files in the tested local copy match the workspace byte-for-byte.
All R1–R8 and M1–M6 items are complete, with the explicit retained-code and compatibility dispositions
above. No implementation work remains in this plan; release/versioning decisions remain with the owner.

## Pre-commit Meridian review — 2026-09-21

Reviewed the complete tracked dirty diff against HEAD plus the new raw-output implementation,
regression tests, this plan, and the inventory of untracked local files. This includes the
concurrent SQLite private-cache change, bridge worker-thread execution/contextual assembly
resolution, and callback cancellation guard. No remaining blocking correctness or Meridian
architecture findings were identified in this review.

Two documentation leftovers were corrected: root README example commands still targeted the
removed `typescript/` directory, and this plan's opening status still said implementation had
not started. No production symbols were changed during this review.

Validation on the refreshed local verification copy:

- `task check` passed: Core 440, bridge 85, Terminal 91, external consumer 8, packets 7,
  package consumer 1 — **632 tests**, no failures. Formatting, analyzers and build passed
  with zero build warnings/errors.
- Plumb returned no findings. `git diff --check` passed on the workspace.
- Fresh ARM64 publish and allowlisted runtime staging passed.
- Native Node smoke with the existing built TypeScript SDK passed raw correction, SQLite
  persistence and accepted-value inspection against the freshly staged bridge.
- An additional native Node probe passed four concurrent interaction pipelines sharing one
  SQLite ledger, with asynchronous JavaScript callbacks and accepted-record inspection.
  This exercises the thread/load-context change in Node, beyond the .NET test host.
- The required GitNexus comparison against `main` ran and reported CRITICAL/280 processes.
  Its stale index includes removed paths, so that count is not reliable dirty-diff evidence;
  direct source review and execution support this assessment. No commit was made.

Release considerations: the intentional removal of published `PacketCommand` and
`PacketValidator` is breaking and remains documented in the Advanced migration section.
Choose the release version accordingly. Include the new source/tests and this plan when staging;
exclude local packages, IDE metadata and `docs/.DS_Store`. The untracked `.threadkeeper` plans
and generated `.claude`/`CLAUDE.md` tooling are separate local material, not additional runtime
changes reviewed for inclusion in this release. Nothing was committed, tagged or pushed.
