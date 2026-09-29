# Tandem slop cleanup spec

Status: decisions final (2026-09-29, §4); executing.
Baseline: `main` @ `b4a824d` (2026-09-29). Line numbers are from that commit; re-locate by symbol name if they have moved.
**Owner WIP:** the main checkout has uncommitted edits in `.github/workflows/release.yml`, `CONTRIBUTING.md`, `docs/quickstarts/typescript.md`, `scripts/runtime-assets.mjs` and `scripts/stage-runtime.mjs`. All work happens in worktrees branched from `b4a824d`; the main checkout is never touched. No WP edits those five files. X4's script and release parts are deferred until the owner has committed that WIP; record them as follow-ups.
Source: independent slop hunt (2026-09-29, four read-only agents plus spot verification), cross-checked afterwards against `docs/plans/post-baseline-remediation.md`. Tags: `NEW`, or `ALREADY-COVERED (Mn)` where that plan audited or decided the item.

This file is self-contained. A worker (human or subagent) should be able to pick up one work package (WP) from this file alone.

---

## 1. Objective

Remove hand-rolled machinery, unearned seams, duplication, dead code and ceremony across Tandem, and fix the bugs the hunt found. Estimated: about 5–6k LOC in `src/` and `bridge/`, plus about 2.5–3k LOC in tests and about 1.5k lines of hand-maintained API manifests.

Where the size is:
- `src/Tandem`: `AgentBlock.cs` 1,992, `PipelineStep.cs` 1,615, `PipelineMessage.cs`.
- `src/Tandem.Ledger`: `SqliteLedgerStore.cs` 1,667.
- `src/Tandem.Advanced`: `HarnessAgentImplementation.cs` 1,044, `AdvancedPipeline.cs` 986.
- `src/Tandem.Terminal`: `TerminalRenderer.cs` 863.
- `bridge/`: `RegistrationContractValidator.cs` 822.
- Tests: 33 hand-written `IChatClient` fakes.

**Breaking changes are allowed and preferred over shims.** Tandem's consumers are its owner's own projects. Never add or keep a compatibility layer: no `[Obsolete]` period, no forwarding aliases, no dual code paths. Remove the old thing, update tests, docs, examples, `ExportedApi.txt`/`PublicApiMembers.txt` (or their replacement, D4), and record the change in `CHANGELOG.md` under "Unreleased — breaking" (create the file if absent).

Non-goals: no new features; no generalised durability or resume machinery (AGENTS.md); no second scheduler or agent loop (MAF owns execution); no release, publish, tag or push.

### 1.1 Invariants (from `AGENTS.md` / `CONTRIBUTING.md`; every WP must preserve them)

```text
Facts in state.
Decisions in routes.
Permissions in capabilities.
Humans in interactions.
Runtime mechanics below the seam.
```

- MAF owns live workflow execution, agent loops, sessions and tool dispatch.
- Active runs are process-owned. Do not reintroduce generalised durability.
- Core versus Advanced is a semantic boundary, not a complexity tier. No runtime bookkeeping in `TState`.
- Keep declared input failures distinguishable from unexpected faults. Never infer permissions from tool-name prefixes.

### 1.2 Consumers outside this repo (check before any public-API change)

- **`~/Sites/cadence`** uses the public Advanced API (at least `LocalProcess`, `PacketFile`; `GitProcess`/`VerificationOperation` wrap `LocalProcess`). Before removing or reshaping any public member, `grep -rn "<Symbol>" ~/Sites/cadence`. If it's used there, record a Cadence follow-up in the ledger; don't edit Cadence from this spec.
- **`~/Sites/tandem-ts`** (published as `@maxanstey-meridian/tandem` 0.2.0) loads `bridge/` in-process via node-api-dotnet from vendored runtime bundles (`runtime/darwin-arm64`, `runtime/linux-x64`). It depends on:
  - the registration contract JSON (`contractVersion: 10`, validated TS-side with zod at `src/index.ts:~1478`);
  - the callback protocol: the `"TANDEM_CALLBACK_CONTRACT:"` marker at `bridge/RegisteredGraphBridge.cs:311-322`, scraped at tandem-ts `src/index.ts:133-172`; cancellation detected by message regex at `:139`;
  - `RunRegisteredGraphAsync`'s JSON result;
  - accepted-value inspection.
  Bridge changes that alter any of these need a matching tandem-ts change and rebuilt runtime bundles (D5). Its slop spec deferred "G1: bridge callback error protocol" to this work.

## 2. Doctrine (inline summary — Meridian)

- **Libraries before machinery.** Use MAF and Microsoft.Extensions.AI for agent/tool mechanics:
  - `AIFunctionFactory.Create`
  - `AIJsonUtilities.CreateJsonSchema`
  - `ChatResponseFormat.ForJsonSchema`
  - `FunctionInvokingChatClient` / `AsBuilder().Use`
  Use System.Text.Json for contracts:
  - `[JsonPolymorphic]`/`[JsonDerivedType]`
  - `JsonStringEnumConverter`
  - `RespectRequiredConstructorParameters`, `RespectNullableAnnotations`
  - `UnmappedMemberHandling.Disallow` (already on `TandemJson`)
  - `Utf8JsonReader` with `AllowMultipleValues`
  - `JsonElement.DeepEquals`
  Plus Microsoft.Data.Sqlite's own busy handling, Spectre.Console (already a dependency), and `Microsoft.Extensions.FileSystemGlobbing`.
- **Seams must be earned.** No single-implementation interfaces without a real seam, no forwarding shells, no public copies of internal types with conversion switches, no telescoping overloads kept for tests.
- **Let patterns prove themselves.** Extract at ≥3 copies or when copies have already drifted (many here have).
- **Strict types.** No `object`/anonymous-object/`JsonElement` value carriers where the type is known. `sealed record`s. Enums, not magic strings.
- **Invariants and concurrency:** the database owns atomicity (`BEGIN IMMEDIATE`, constraints, conditional writes). No hand retry loops layered on the driver's own.
- **No self-narrating comments, no test hooks in production code.**
- **Tests:** real behaviour on the real engine (real SQLite, real in-process MAF, as today); shared fakes, not 33 copies; no sleeps as ordering; mechanical boundary checks, not source-text greps.

## 3. Ground rules for every WP

1. **GitNexus** (CLAUDE.md/AGENTS.md): `impact` upstream before editing a symbol; report direct callers, affected processes and risk, and warn on HIGH/CRITICAL (record and proceed; the owner approved this plan). Run `detect_changes` before each commit. Never rename with find-and-replace. Refresh the index first if stale (`node .gitnexus/run.cjs analyze`).
2. **Red first** for every bug in §5.
3. **Behaviour-preserving for cleanups** unless the WP says otherwise. Existing tests pass, or are rewritten to the new API in the same WP.
4. **Stay in your lane** (§6). Record cross-lane needs as follow-ups.
5. **Grep to prove dead**, across `src`, `bridge`, `bridge-tests`, `tests`, `examples`, `~/Sites/cadence` and `~/Sites/tandem-ts/src`. Record the evidence.
6. **Gate:** `task check` (format, analyzers, all test suites; see `Taskfile.yml`). Bridge WPs also run the bridge tests and stage the runtime (`scripts/stage-runtime.mjs`). Then Plumb: `~/Sites/plumb/plumb . --json`. Fix errors; fix warns or record the exception.
7. **Commits:** Wave 1 lanes work in their own git worktree on `slop/<lane>` branched from `slop/wave0`, one commit per WP. Never commit on `main`.

---

## 4. Decisions (final, 2026-09-29)

| ID | Decision | Recommended | Affects |
|---|---|---|---|
| D1 | The ledger has grown a general-purpose store: typed `LedgerStream<T>`/`LedgerDocument<T>`, a `ledger_contracts` registry, versioned documents with optimistic concurrency, ambient transactions, idempotent replay, and `ReopenRunAsync` ("cannot resume from status"). Production writes only `PipelineJournal.Stream`; documents, reopen and read-after/recent are used only by tests. This runs against "do not reintroduce generalised durability". | **Owner: durability is a tombstone; runs are in memory.** Remove all durability: documents, contracts registry, optimistic concurrency, idempotent replay, reopen/resume, generic streams, ambient transactions. The ledger is an append-only run-history journal for inspection (tandem-ts `ledgerPath`/`inspectAccepted`, ledger tools, Cadence host history): create run, append, complete, read accepted/entries. | L1, W2-8 |
| D2 | Ledger data access: hand-written ADO.NET mapping with near-identical methods. | **Dapper** for queries (doctrine: SQL-heavy projections), plus one write-transaction helper. | L2 |
| D3 | Terminal hand-renders JSON (fence detection, brace scanner, tokenizer, highlighter, wrapper). | **`Spectre.Console.Json`** (`JsonText`) plus `Utf8JsonReader { AllowMultipleValues = true }` to split documents. | T1 |
| D4 | Public API tracking: a 476-line reflection renderer test plus about 1.5k lines of hand-maintained `ExportedApi.txt`/`PublicApiMembers.txt`, regenerated via an env switch. | **`Microsoft.CodeAnalysis.PublicApiAnalyzers`** (`PublicAPI.Shipped.txt`/`Unshipped.txt`, build-time RS0016/RS0017), plus one small test asserting the public API text contains no `Microsoft.Agents`. | W2-4 |
| D5 | Bridge callback error protocol: replace message-marker scraping and regex-matched cancellation with a result envelope. Needs a tandem-ts change, rebuilt runtime bundles and a tandem-ts release. | **Do it**, coordinated in W2-6: .NET envelope, tandem-ts consumer, bundles, both sides' tests. | W2-6 |
| D6 | CI never runs `task check`; workflows only trigger on tags, and `release.yml` builds bridge bundles without tests. | **Add** a `check.yml` on push/PR that runs `task check` plus the bridge tests. Make release/publish depend on it. | X4 |
| D7 | Structured-output correction count: code allows 2 (`AgentBlock.cs:35` `StructuredOutputCorrectionLimit = 2`); `CONTRIBUTING.md:143` promises "one corrective response". | **Make the code match the documented invariant (1).** If the owner prefers 2, fix the doc instead. | W0-1 |
| D8 | `Inspect()` renders both Mermaid and DOT every call; nothing outside tests reads either. Both are public and listed in CONTRIBUTING. | **Keep Mermaid, drop DOT**, and render lazily. | K6 |
| D9 | Workspace grep ignores `.gitignore`: it prunes a hard-coded list of about 70 directory names. M4 audited and kept the glob/walk. | **Respect ignores** inside a git repo via `git ls-files -co --exclude-standard` (git is already a dependency). Keep the prune list only outside git. Replace hand glob→regex with `FileSystemGlobbing.Matcher`. Behaviour change; record it. | A5 |
| D10 | `examples/getting-started/csharp` (4 projects): not in `Tandem.slnx`, the Taskfile or CI; references `Meridian.Tandem 0.1.0` from NuGet; its README points at a moved `typescript/` dir. | **Add it to `Tandem.slnx`** with project references, as the other examples are, and fix the README. | X3 |
| D11 | The shipped bridge is still named `Tandem.NodeApiSpike` (namespace, csproj, test project). | **Rename to `Tandem.Bridge`** in W2-6, together with D5, because tandem-ts's runtime loader paths change too. | W2-6 |
| D12 | `StreamRetryChatClient` (hand-written retry/backoff) may stack on `System.ClientModel`'s default `ClientRetryPolicy` (3 retries). | **Measure first.** Keep exactly one retry layer. Prefer disabling the SDK retry for the streaming path and keep the small buffered-retry loop only if `Microsoft.Extensions.Resilience` doesn't fit stream buffering. No new dependency unless it deletes code. | A7 |

---

## 5. Bugs (fix first, red-first)

| ID | Bug | Evidence | WP |
|---|---|---|---|
| BUG-1 | Bridge accepted-value inspection has no `stream = 'runtime.journal'` filter: any other stream's rows are deserialised as `RuntimeJournalRecord`. It orders by per-stream `sequence`, not globally. It also duplicates the ledger schema in bridge SQL. | `bridge/RegisteredGraphBridge.cs:13-63` | W0-1 (fix by calling a new store `ReadAcceptedAsync`) |
| BUG-2 | The terminal runner silently swallows a failed `CompleteRunAsync` (`catch { }`), while the non-terminal path raises. | `src/Tandem.Terminal/TerminalPipelineRunner.cs:113-116` (fed by `RegisteredGraphBridge.cs:203-210`) | W0-1 |
| BUG-3 | Bridge native loading hardcodes `libe_sqlite3.dylib`, so Linux is broken despite a linux-x64 bundle. It also eagerly loads every DLL in the folder. | `bridge/NodePipelineBridge.cs:27-56` | W0-1 (`NativeLibrary.TryLoad` with per-RID name, or `AssemblyDependencyResolver`/`.deps.json` probing) |
| BUG-4 | Two tests hardcode `.../bin/Debug/net10.0/*.dll`, so they fail under `-c Release`. | `tests/.../SqliteLedgerStoreTests.cs:1242`, `LocalProcessTests.cs:205` | W0-1 |
| BUG-5 | `copy_file`, `move_file` and `create_directory` are accepted by the workspace switch but missing from `_reservedWorkspaceToolNames`, so a user tool colliding with them isn't detected. | `src/Tandem/Infrastructure/Blocks/AgentBlock.cs:37` vs `:1537` | W0-1 |
| BUG-6 | Correction count vs documented invariant (D7). | `AgentBlock.cs:35`, `CONTRIBUTING.md:143` | W0-1 |
| BUG-7 | The source generator defeats incremental caching: `StepModel`/`StepGenerationResult` are classes without value equality, and the transform output carries a `Diagnostic` with a `Location`. | `src/Tandem.Generators/PipelineStepGenerator.cs:186-233` | K7 |
| BUG-8 | `git_blame` output is cut at 500 lines with no `nextOffset`, unlike every other paged tool. `Page()`'s `startLine` is always 1. | `src/Tandem.Advanced/ReadOnlyGitTools.cs:461-502` | A4 |
| BUG-9 | Structured-output schema generation ignores `TandemJson` (the documented single canonical contract): it uses JsonSchema.Net's own camelCase resolver, so enum and naming rules can drift from actual deserialisation. | `src/Tandem/.../StructuredOutputSchema.cs` vs `AgentCapabilities.cs:289` | K2 |
| BUG-10 | The git tools' path check compares `.git` case-sensitively and skips the symlink check, unlike `WorkspacePathAuthority`. | `ReadOnlyGitTools.cs:391-412` vs `HarnessAgentImplementation.cs:290-349` | A4 |
| BUG-11 | Ledger search `Matches` runs `Contains` over raw JSON text, so queries can match property names. Filters are full-table scans in C#. | `src/Tandem.Ledger/SqliteLedgerStore.cs:288-319,489` | L3 |
| BUG-12 | The example `examples/code-writer/csharp/Program.cs:35` writes `code-writer-ledger.sqlite3` into the current directory, leaving a stray repo-root artefact. | — | X3 |
| BUG-13 | `TaskCanceledException` ("A task was canceled.") doesn't match tandem-ts's cancellation regex, so cancellation works only when `signal.aborted` is set. `FindCallbackContractException` ignores `AggregateException.InnerExceptions`. | `bridge/RegisteredGraphBridge.cs:252-258,301-309`, `CallbackDispatcher.cs:62` | W2-6 (fixed by the envelope, D5) |

---

## 6. Work packages

### Waves and lanes

- **Wave 0 (serial, one worker, branch `slop/wave0` from `main` after the prerequisite):** W0-1 → W0-2 → W0-3.
- **Wave 1 (parallel worktrees from `slop/wave0`, file-disjoint):**
  - **Lane K — Core:** owns `src/Tandem/**`, `src/Tandem.Generators/**`. Order: K1 → K2 → K3 → K4 → K5 → K6 → K7.
  - **Lane A — Advanced, OpenAICompatible and Packets:** owns `src/Tandem.Advanced/**`, `src/Tandem.OpenAICompatible/**`, `src/Tandem.Packets/**`. Order: A1 → A2 → A3 → A4 → A5 → A6 → A7 → A8.
  - **Lane L — Ledger:** owns `src/Tandem.Ledger/**`. Order: L1 → L2 → L3 → L4.
  - **Lane T — Terminal:** owns `src/Tandem.Terminal/**`. Order: T1 → T2 → T3.
  - **Lane B — Bridge (.NET only):** owns `bridge/**`, `bridge-tests/**`. Order: B1 → B2. The protocol change waits for W2-6.
  - **Lane X — Tests, examples, scripts, CI:** owns `tests/**` (helper code and call sites; don't move behaviour tests between files), `examples/**`, `scripts/**`, `Taskfile.yml`, `.github/**`, `.gitignore`. Order: X1 → X2 → X3 → X4.
  - Lanes may add tests for their own area. Lane X must not restructure suites the other lanes are editing in Wave 1.
- **Wave 2 (serial, on `slop/integration` after merging lanes in the order X, L, T, A, K, B):** W2-1 → W2-6, W2-8, then W2-7 last.

Each WP lists: **Tag · Est. LOC saved**, then Problem/Change/Acceptance (compressed where obvious).

---

### Wave 0

#### W0-1 — Bugs 1–6
NEW

- **BUG-1:** add `SqliteLedgerStore.ReadAcceptedAsync(runId)` on a read-only connection, filtered to the journal stream and ordered by global insert order; the bridge calls it and its SQL is deleted.
- **BUG-2:** surface the `CompleteRunAsync` failure the same way as the non-terminal path. The full shared finishing helper comes in W2-2.
- **BUG-3:** RID-aware native loading. Test on osx-arm64 and linux-x64 (CI or a container).
- **BUG-4:** locate test workers via the referenced project's output (MSBuild `$(OutputPath)` → assembly metadata, or `AppContext.BaseDirectory` for copied references).
- **BUG-5:** one name → kind/effect table for workspace tools, used for both reservation and dispatch. The full tool-table consolidation is K4.
- **BUG-6:** D7.

**Acceptance:** a red test per bug. `task check` green.

#### W0-2 — Dead code sweep
NEW · ~450 LOC

Delete, after re-verifying each by grep (§3.5):
- **Core:**
  - `GeneratedStepExecutor<TState,TResult>` (`PipelineStep.cs:290-322`)
  - `GeneratedPassThroughStepDescriptor.Bind(StandardOutcomeRouteAwareness)` (`:240`)
  - `PipelineMessage.WithOutcome` (`PipelineMessage.cs:22`)
  - `ToolOutcomeCollector.LifecycleToolName` (`AgentBlock.cs:1980`)
  - `AgentUsage.LastModelCallDuration`/`CheckpointAtTokens` (write-only)
  - the unused `using …Checkpointing` (`WorkflowRunner.cs:3`)
  - empty `IPipelineExecutionContext`/`PipelineExecutionContext` and the ignored delegate parameter (`PipelineInteraction.cs:38,76`)
  - the `CorePipelineNodes` forwarding shell
  - the `AgentOperation(Func…)` ctor used only by `GeneratedAuthoringVerticalTests.cs:261`
- **Advanced:**
  - `BoundedTextPageReader.ReadAsync(IAsyncEnumerable<string>)` + `StreamedPageAccumulator` (`BoundedTextPageReader.cs:23-37,205-292`)
  - `StructuredOutputPolicy.Parse`, `StructuredJsonExtractor` (a verbatim copy of core `AgentStructuredJsonExtractor`), `StructuredOutputAcceptancePolicies`, `WithStructuredOutput`, `WithMessageFromContext` + `AdvancedAgentMessage` (`StructuredOutput.cs:63-209`, `AdvancedPipeline.cs:108,630,752`). M1 relied on these as the Advanced seam, but nothing in tandem, Cadence or tandem-ts uses them. Update `ProjectBoundaryTests.cs:238`.
  - `SearchDiagnostics` and its hooks (`WorkspaceGrepTools.cs:102,181,183,284,433-437`)
  - `PipelineOperation.ObserveCommandOutputAsync` (`AdvancedPipeline.cs:955-966`)
  - `PositionedTextReader.Peek()`
- **Ledger:** `RunLedger.AppendAfterTerminalAsync` (`RunLedger.cs:56-78`), `RunLedger/SqliteLedgerStore.ReadRecentAsync` (`RunLedger.cs:91-95`, `SqliteLedgerStore.cs:1116-1171`).
- **Bridge:** `NodePipelineBridge.EnumerateNodeContracts` (`RegisteredGraphBridge.cs:391-403`); the always-null `presentation` parameter of `RegisteredRunObserver.Compose` (`:164`).
- **Terminal:** `TerminalSnapshot.Summary/ActiveStep/InputTokens/OutputTokens/WaitingInteractions` (`TerminalModel.cs:39-59,81-86`). They're computed, never rendered, and read only by tests; rewrite those tests against rendered output.
- **Tests:**
  - `tests/Tandem.Tests/Infrastructure/NoOpWorkflowContext.cs` (unreferenced)
  - unused `Spectre.Console`/`Spectre.Console.Testing` package references in `Tandem.Tests.csproj`
  - `ProjectBoundaryTests` checks for names that no longer exist (`Tandem.Delivery` ×10, `DeliveryState`, `RouteRegistry`, `RouteMap`, `GeneratedCustomStepDescriptor`)

**Acceptance:** grep evidence in the ledger. API manifests updated. `task check` green.

#### W0-3 — Repo hygiene
NEW

- Add `local-nupkgs/` to `.gitignore` (it's untracked and not ignored, so it could be committed by accident). Delete the local stray `samples/` (only `obj/` left over) and the empty `ledger.sqlite3`.
- `CONTRIBUTING.md`: fix the correction-count sentence if D7 goes the other way. Edit only after the owner's WIP is committed.

---

### Lane K — Core

#### K1 — Tool middleware and `AgentBlock` structure
NEW · ~250 LOC

- One local `FinishAsync(status, evidence, updateText)` replaces the 4 Faulted blocks (`AgentBlock.cs:701-729,811-839,902-939,954-982`) and the 2 Blocked blocks (`752-796,840-877`).
- `ResolveOutcomeAsync` (`:1150`) returns `Task.FromResult` 5 times and never awaits: make it synchronous or `ValueTask`.
- `8000` appears 7 times: name it once.
- Remove the `onUpdate` ctor parameter (production always passes `null`; only `LocalCapabilityTests.cs:106` uses it) and the `FunctionResultContent` branch in `PublishUpdatesAsync` (`:1625-1637`) that exists only for it.
- Split `ExecuteAsync` (69–577) into named phases, and move `PersistedToolInvocation`/`PersistedProcessEvidence`/`ToolOutcomeCollector` out of the file.
- **Investigate `ToolResultAdjacencyChatClient` (~200 LOC) + `MessageInjectingChatClient` (`:534-554`):** they exist because Tandem sets `Terminate = true` (`:1085`) and injects the tool result itself. If MAF's terminating function-invocation path already records `FunctionResultContent` in the session, delete both. Prove it with a real MAF session test. Otherwise record why they stay.

#### K2 — One JSON-schema generator (fixes BUG-9)
NEW · ~45 LOC plus one package

Replace the `JsonSchema.Net.Generation` path in `StructuredOutputSchema.cs` (including `StrictObjectRefiner`) with `AIJsonUtilities.CreateJsonSchema(typeof(T), serializerOptions: TandemJson.TypedContract, inferenceOptions: new() { TransformOptions = { DisallowAdditionalProperties = true, RequireAllProperties = true } })`, as `CapabilityFunction.CreateInputSchema` (`AgentCapabilities.cs:289`) already does. Remove the package from `Tandem.csproj`.

**Acceptance:** red test for BUG-9 (an enum or naming-policy case where the old schema disagrees with `TandemJson` deserialisation). Snapshot the existing output schemas and diff them; record every difference.

#### K3 — Executors, runtime state and acceptance
NEW · ~430 LOC

- **Executors:** `GeneratedPassThroughStepExecutor` (`PipelineStep.cs:324`), `GeneratedStateStepExecutor` (`:380`) and `GeneratedOutcomeStepExecutor` (`:438`) become one executor, with the accepted-value selector as a parameter.
- **Duplicated builder code:** `PipelineBuilder.Create<TResult>` duplicates `Bind(node)` (`:960-1006` vs `:1412-1465`). Keep one `WithDescription` block instead of 3.
- **Failure status logic:** unify the "Succeeded then recompute from route awareness" and failure `PipelineStepCompleted`/`FailureEvidence` logic across `ParallelJoinExecutor` (`PipelineParallel.cs:448-537`), `AdaptFailed` (`PipelineStep.cs:511`) and `PipelineObservationPublisher` (`PipelineObservationRuntime.cs:385-396`).
- **`PipelineRuntime`** (`PipelineMessage.cs:31-310`): five parallel per-step dictionaries plus a mutable `HashSet` in a record become `ImmutableDictionary<string, AgentStepRuntime>`, where `AgentStepRuntime` is a `sealed record` (Session, ToolInvocations, Usage, Profile, Latches, Count), updated with `SetItem`/`Remove`. Delete the 10 `With*`/`Without*` methods, the 6-way `Merge` and the `"{stepId}:{gateId}"` string keys. Deduplicate the "drop session + tools + usage" chain (`AgentBlock.cs:1711,1724,1742`).
- **Tool invocations:** store `IReadOnlyList<ToolInvocationObservationDescriptor>` directly instead of `JsonElement`. The runtime is never serialised (grep: 0), so delete the `PersistedToolInvocation`/`PersistedProcessEvidence` mirrors (`AgentBlock.cs:1761-1853`).
- **Acceptance:** one async structured-output acceptance taking a `sealed record` context replaces the sync+async pair with 7–8 positional args (`AgentBlockConfig.cs:86-128`, `AgentSdk.cs:460-530`). Keep `TOutput` in closures instead of `object` (`Candidate object?`, `Apply Func<TState, object, TState>`, `EmitAccepted(object)`, casts at `AgentSdk.cs:324,332`).
- **Config values, not delegates:** replace `configureChatOptions` (it only sets `ResponseFormat`, derivable from `StructuredOutput.JsonSchema`) and `configureModelRequestOptions` with values on `AgentBlockConfig`. Build `ChatOptions` in one place and drop the `configureStructuredOutput` flag (`AgentBlock.cs:1324`).

#### K4 — Tool errors, names and capabilities
NEW (M5 audited input validation only) · ~200 LOC

- **One tool-error shape:** one `sealed record ToolError(Code, Message, Problems)` with a single serialiser replaces the 5 shapes (`CapabilityFunction.Error`, `CapabilityAcceptanceRuntime.Error`, `JsonCapabilityFunction.ValidationError`, `ToolInputValidation.Error`, the gate error at `AgentBlock.cs:788`). Replace `IsToolError`'s string sniffing (`:1103`, `:1142`) with the typed result.
- **One capability flow:** `CapabilityFunction` and `JsonCapabilityFunction` share validate → summarise → context → `AcceptAsync`. A typed capability is a JSON capability whose validate step is "deserialise + FluentValidation".
- **One problem record:** one record replaces `AgentStructuredOutputProblem`/`AgentJsonValidationProblem`/`PipelineStructuredOutputProblem`, with one FluentValidation → problem mapper (currently ×6).
- **One tool-name table:** one name → kind/effect table for workspace, git, shell, web, ledger and skill tools (`AgentBlock.cs:37,58-60,99,111-113,1349-1406,1508-1563`), completing BUG-5's table. Ledger tool *construction* leaving Core is W2-3.
- **Small formats:** keep one each of the capability-ID format, accepted-call-ID format and object-root schema guard (`AgentCapabilities.cs:87,244`, `AgentJsonCapabilities.cs:39,53`, `CapabilityAcceptanceRuntime.cs:40`, `AgentSdk.cs:372`). Replace the 8× `SerializeToElement(new { })` with a static empty object.

#### K5 — Runner API and JSON extraction
NEW · ~130 LOC

- **Runner API:** `InProcessPipelineRunner` has 6 telescoping `RunAsync` overloads (`WorkflowRunner.cs:34-133`). Production uses 2; make it one static method with optional parameters. Remove `catch (Exception ex) when (failure is not null) { _ = ex; }` (`:278`).
- **JSON extraction:** `AgentStructuredJsonExtractor` (`StructuredOutputPolicy.cs:147-193`) hand-scans braces. Find the first `{`, then use `Utf8JsonReader` + `JsonDocument.ParseValue` (one value, ignores trailing text).

#### K6 — Inspection output (D8)
NEW · ~40 LOC

Keep Mermaid, drop DOT, and render lazily (`PipelineStep.cs:766-846`). Update CONTRIBUTING, docs and tests.

#### K7 — Generator caching (fixes BUG-7)
NEW · ~35 LOC

Equatable `sealed record` models and a location-free diagnostic model in `PipelineStepGenerator.cs`. **Acceptance:** an incremental-generator test (`GeneratorDriver` with `trackIncrementalGeneratorSteps: true`) shows the steps cached on an unrelated edit.

---

### Lane A — Advanced, OpenAICompatible and Packets

#### A1 — Workspace command tool
NEW (argument semantics settled by M3) · ~120 LOC

`WorkspaceCommandFunction` (`HarnessAgentImplementation.cs:597-777`) has several problems:
- it builds a schema from anonymous objects and picks arguments out of raw JSON by hand;
- it runs `/bin/zsh -lc` (a login shell, unlike `run_shell`'s MAF `LocalShellExecutor`);
- it round-trips results through JSON into a private `ShellResultEvidence` (`:779-826`).

Replace it with `AIFunctionFactory.Create((string[]? arguments) => …)` with `[Length]` attributes (the schema comes from `AIJsonUtilities`), run via `CreateExecutor(...).RunAsync(quotedCommand)`, and use MAF's typed `ShellResult`. Preserve M3's semantics (fixed command text plus bounded optional model arguments, quoting).

**Acceptance:** M3's execution and quoting tests pass. Shell behaviour now matches `run_shell`; record it.

#### A2 — File stores
NEW · ~110 LOC

`BomlessFileSystemAgentFileStore` (`:848-891`) wrapped by `GitExcludedFileStore` (`:893-1042`) becomes one store covering write/read/delete/exists:
- both strip the BOM;
- `.git` is already rejected by `WorkspacePathAuthority`;
- `SearchAsync`, `LooksBinary` and the `ListChildrenAsync` filter are unreachable, because grep, ls and read are always replaced by Tandem's tools (`:49-55`).

Verify reachability against MAF first (reflect or test which store methods MAF's remaining tools call).

#### A3 — Registration, pagination and shared path check
NEW · ~120 LOC

- **One `AddTool(options, effects, tool, effect, evidence)`:**
  - it does the duplicate-name check that git/read/registered/Tavily have and grep/ls/shell/mutation lack;
  - the identical if/else branches (`:73-101`) collapse;
  - one kind → name/effect/evidence table replaces the three spellings (`HarnessToolEffects :189-239`, `IsMutation :168`, `AgentTools._builtIns` `AdvancedPipeline.cs:417`), sharing Core's K4 table if exposed; otherwise record a follow-up.
- **One pagination helper and named records:**
  - replaces the copies of the `limit` validation + `PaginationValidationException { retryOffset, retryLimit }` (`ReadOnlyGitTools.cs:103-120`, `WorkspaceGrepTools.cs:108-121`, `WorkspaceListTools.cs:45-56`) and the 64k budget loop (`ReadOnlyGitTools.cs:154-165`, `WorkspaceGrepTools.cs:218-236`);
  - named records replace the anonymous `object`s: `GitStatusPage`, `ListEntry`, `RetryHint` instead of `Task<object>` (`:94`), `IReadOnlyList<object>` (`WorkspaceListTools.cs:11`) and the exception payloads.
- **Path check:** `ReadOnlyGitRepository.ValidatePath` calls `WorkspacePathAuthority.Resolve`, then `Path.GetRelativePath` (BUG-10 red-first).

#### A4 — Git tools (fixes BUG-8, BUG-10)
NEW (the CLI choice is justified: SHA-256 repos, worktrees, user config, no native deps) · ~70 LOC

One run helper with an optional output path replaces `RunAsync`/`RunPagedAsync` (`:414-459`, `:504-585`) and their duplicated timeout, truncation and bad-revision mapping. Remove the `createTempFile` test hook (`:79`, used only by `ReadOnlyGitToolsTests.cs:214`). `git_blame` pages with `nextOffset` (BUG-8).

#### A5 — Grep (D9)
ALREADY-COVERED (M4 audited and kept the glob/walk). Reopened only for D9 · ~100 LOC

- Use `Microsoft.Extensions.FileSystemGlobbing.Matcher`, prefixing `**/` onto slash-less patterns, instead of `GlobRegex`/`LiteralPathPrefix` (`WorkspaceGrepTools.cs:370-431`).
- Use `FileSystemEnumerable<T>` with recurse/include predicates instead of the recursive walk.
- Respect ignores via `git ls-files -co --exclude-standard` inside a repo.

Preserve M4's audited guarantees: bounded output, cancellation, symlink/.git exclusion, explicit path overrides, the no-snapshot guarantee, and regex timeout.

#### A6 — Duplicate public types (with Core's internals: see W2-1)
NEW

Only the Advanced-internal parts here:
- write `ToContext`/`ToOutcome` once (`AdvancedPipeline.cs:942-950`, `StructuredOutput.cs:346-359`);
- `AgentMessageOutcome` (`:94`) equals `OperationOutcome` (`:49`), so keep one.

The public-vs-internal enum copies need Core, so they are W2-1.

#### A7 — OpenAICompatible (D12)
NEW · ~50 LOC

- **Measure and remove stacked retries:** check whether `System.ClientModel`'s default retry stacks on `StreamRetryChatClient` (`StreamRetryChatClient.cs:21-148`). Keep exactly one layer, per D12. `RequestDeadlineChatClient`: keep it unless the retry change subsumes it.
- **Drop reflection if possible:** `ReasoningExtractionChatClient.cs:16-20,115` reads `StreamingChatCompletionUpdate.Patch` by reflection. Use the public member if the SDK exposes it (it's used publicly on `ChatCompletionOptions.Patch` at `:102` under the same `SCME0001` flag); otherwise record why reflection stays.

#### A8 — Packets and small items
NEW · ~150 LOC

- **Overloads:** `PacketFile` has 6 `Parse`/`Read`/`ReadAsync` overloads differing only by validator (`PacketFile.cs:17-55`). Use one optional `IValidator<T>? validator = null`, and drop the synchronous `ReadText` duplicate (`:423-508`). Check Cadence's usage first.
- **Scalar typing:** `NormalizeScalar`/`IsNonFiniteYamlNumber` (`:275-356`) re-implement YAML core-schema typing, and results go YAML → dictionary → JSON string → `T` (`:95-96`). Build a `JsonNode` directly and `Deserialize<T>(JsonNode)`, or use YamlDotNet's typed scalar resolution. Reject `<<` merge keys, as tandem-ts does. Replace `ShapeMessage`'s exception-text sniffing ("System.String", `:574-589`).
- **BOM detection:** `PositionedTextReader` hand-detects the BOM (`:29-58`); use `new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true)`, as `BoundedTextPageReader.cs:59` does. This is ALREADY-COVERED (M4 audited BOM handling); change it only if the test for strict invalid-UTF-8 failure still passes.
- **Test hooks:** remove `TavilyWebTools.Add(createTools, getEnvironmentVariable)` (`:15-16`) and its pass-through in `HarnessAgentImplementation.Create` (`:18-19`); `WorkspaceShellTools.Add(timeout, maxOutputBytes)` (`:562-563`); and `CreateExecutor(acknowledgeUnsafe)`. Use constants or the existing options.
- **Validation:** trim `LocalProcess`'s 45-line argument validation (`LocalProcess.cs:40-85`). It's public and Cadence uses it, so keep the behaviour.
- **Split:** after A1–A3, split `HarnessAgentImplementation.cs` (about 14 types) by concern.

---

### Lane L — Ledger

#### L1 — Journal store (D1)
NEW · ~600 LOC

Delete `LedgerDocument<T>`, `Read/WriteDocumentAsync`, the `ledger_contracts` table and `EnsureContractAsync` (`:1400-1462`), `ReopenRunAsync` (`:753-781`), generic `LedgerStream<T>` and `ReadAfterAsync`. Rewrite the tests that only exercised them against journal behaviour, or delete them. Bump the schema version and create a fresh schema (no migration from the old one, per no-shims). Record the on-disk incompatibility in CHANGELOG.

#### L2 — Data access and locking (D2)
NEW · ~550 LOC

- **Locking:** delete `RetryLockedAsync` (10 uses), the second retry loop in `ExecuteAsync`, `LockRetryAttempts`/`LockRetryDelay` and their validation. Keep WAL, `BEGIN IMMEDIATE` and a busy timeout (connection-string `Default Timeout`, `Foreign Keys=True`). Fix the misleading `Pooling=false` comment (`:32-35`).
- **Initialisation:** initialise once (`Lazy<Task>`/async-lazy) instead of opening a connection and running PRAGMAs on every read (`:241,295,344,549,623,…`).
- **Dapper:** Dapper `QueryAsync<Row>` replaces the three near-identical read methods (`:1018-1171`) and the two `ReadLatestAcceptedAsync` overloads (`:539-673`). One `WithWriteTransactionAsync(Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>>)` replaces the repeated "scope? InTx : open+begin+commit" pattern (Complete `:783-860`, Append `:862-1016`).
- **`payload_hash`:** drop the column and hashing. The code already compares full payload bytes, so the hash is redundant (`:124,135,896,976-977`).
- **Semaphore:** delete the redundant `SemaphoreSlim` in `SqlitePipelineObserver` (`PipelineJournal.cs:25,199-216`). Appends are already atomic under `BEGIN IMMEDIATE`, and a semaphore doesn't guarantee FIFO anyway.

**Acceptance:** the existing real-SQLite concurrency tests (separate worker processes) pass.

#### L3 — Queries in SQL (fixes BUG-11)
NEW · ~60 LOC

Add generated columns (`kind`, `step_id`, `identity`, `value_type` via `json_extract`) with an index. Filter in SQL instead of loading and deserialising every row (`FindActionEntryAsync`, `ReadLatestAcceptedAsync`, `ReadPageAsync`; resolves the TODO at `:545-547`). Search value text only, not property names: use FTS5, or `instr` on extracted values.

#### L4 — Store returns rows
NEW · ~200 LOC moved

Take agent-tool presentation out of the store:
- page limits;
- 4k/200k truncation;
- `read_ledger`/`search_ledger` error text;
- surrogate splitting;
- stdout/stderr selection;
- the anonymous `object` from `ReadEntryPageAsync` (`:187-510`, `:321`, `:430-442`) and `IPipelineLedgerReader`.

The store returns typed rows. Presentation moves to a ledger-tools type in `Tandem.Ledger` with a named `LedgerEntryPage` record. Core wiring is W2-3.

---

### Lane T — Terminal

#### T1 — JSON rendering (D3)
NEW · ~200 LOC

Use `Spectre.Console.Json` `JsonText` and `Utf8JsonReader { AllowMultipleValues = true }` instead of `TerminalRenderer.cs:288-572` (fence detection, `ExtractJsonDocuments`, `JsonTokens`, the JSON wrapper).

#### T2 — Width-correct wrapping
NEW · ~80 LOC

`WrapVisibleText` counts UTF-16 units (`:585-614`). Use a two-column Spectre `Grid` for the hanging indent, and `Segment.SplitLines(renderable.Render(options, width))` for scroll maths. Manual scrolling stays (Spectre has no scroll view). Delete `Truncate`, which duplicates `Overflow.Ellipsis`. Replace the raw `"\x1b[?25l/h"` (`TerminalPipelineDisplay.cs:194,216`) with `_console.Cursor.Hide()/Show()`.

#### T3 — One observation mapping
NEW · ~70 LOC

Plain mode prints from the model's `TranscriptEntry`/`StepVisit` output instead of re-switching over `PipelineObservation` (`TerminalPipelineDisplay.cs:336-408` vs `TerminalModel.Apply :114-253`). Test deterministically with `FakeTimeProvider` (the Terminal already accepts a `TimeProvider`), and delete the loose timing regex (`TerminalRendererTests:762`).

---

### Lane B — Bridge (.NET, protocol unchanged)

#### B1 — Registration contract as types
NEW (M2 replaced the validator adapter; this is the contract itself) · ~450 LOC

`RegisteredNodeContract` is one flat record keyed by a `Kind` string, which forces about 60 forbidden/`Required` checks (`RegistrationContractValidator.cs:241-403`). Closed sets (`WireApi`, `Effort`, `Outcome`, `Presentation`, `Kind`) are matched as strings, and `ParseAndValidate` parses twice (`:16-30`).

- Use per-kind types via `[JsonPolymorphic(TypeDiscriminatorPropertyName="kind")]` + `[JsonDerivedType]`. Forbidden fields become unknown members, rejected by `TandemJson`'s `UnmappedMemberHandling.Disallow`.
- Use enums with `JsonStringEnumConverter`.
- Turn on `RespectRequiredConstructorParameters` + `RespectNullableAnnotations`.
- Keep only the cross-reference checks: routes, outputs, reachability, callback uniqueness.
- Range checks already owned by Core builders (temperature, reasoning tokens, checkpoint bounds, timeout ceiling, one unconditional route, duplicate IDs) must be enforced once in Core and surfaced through the bridge, not re-implemented.

**Constraint:** contract JSON shape and `contractVersion: 10` unchanged. tandem-ts's zod schema must still produce accepted input. Verify by running tandem-ts's test suite against a freshly staged runtime.

#### B2 — bridge-tests infrastructure
NEW · ~120 LOC

Replace the two `HttpListener` servers on random ports (`OpenAiCompatibleChatClientsTests.cs:185-256`) with a stub transport (`OpenAIClientOptions.Transport`). That needs a transport parameter on `OpenAiCompatibleChatClients.CreateAsync`: a cross-lane Lane A follow-up, or add it here if Lane A agrees. Keep the raw-TCP truncated-body retry test (justified).

---

### Lane X — Tests, examples, scripts and CI

#### X1 — Shared test support
NEW · ~1,200 LOC

Add `tests/Tandem.Tests/TestSupport/`:
- **`TestChatClient`:** response queue, `Requests`, `Options`, optional `GetResponseAsync`/`GetStreamingResponseAsync` delegates. It replaces the 33 `IChatClient` fakes (10 × `ScriptedChatClient`, 4 × `NoopChatClient`, …; 33 empty `Dispose`, 22 streaming shims). `ExternalConsumer`/`PackageConsumer` keep their own copies (they compile against packages).
- **`TempDirectory : IDisposable`:** built on `Directory.CreateTempSubdirectory("tandem-")`. Replaces 48 temp-path sites, 44 `Directory.Delete`s and the 3 private helpers.
- **`RecordingObserver`/`InlineObserver`:** in `TestDefinitions.cs`, next to `CompositePipelineObserver`. Replace the copies at `SqliteLedgerStoreTests:1295`, `TypedStructuredOutputAcceptanceTests:134`, `AgentCommandObservationTests:579`, `JsonAgentContractTests:580`, `PublicRuntimeTests:322` and `InProcessPipelineRunnerTests:1034`.
- **Process runs:** use `LocalProcess.RunAsync` instead of the 3 hand-written runners (`SqliteLedgerStoreTests:1234`, `PackageConsumerTests:521`, `ReadOnlyGitToolsTests:389`).

#### X2 — Deterministic and non-duplicated tests
NEW · ~400 LOC

- **Polling and sleeps:** use `TaskCompletionSource` instead of polling loops (`MafBindingCharacterizationTests:459`, `InProcessPipelineRunnerTests:992`). Use explicit gates instead of sleep-ordering (`ParallelPipelineRunnerTests:33`, `CollectionTests:32`), and deterministic signals instead of "nothing happened" sleeps (`TerminalPipelineDisplayTests:547`, `LocalProcessTests:77`).
- **MAF characterisation:** tag `MafBindingCharacterizationTests` `[Trait("Category","MafCharacterization")]`, and remove its duplication with `InProcessPipelineRunnerTests:867` and its pins of Tandem string constants (`:14`).
- **JSON contract tests:** `JsonAgentContractTests` `JsonOutput_*`/`JsonCapability_*` mirror the typed suites; make them one `[Theory]` over contract kind. Keep one concurrent-run isolation test instead of 4.
- **Embedded C#:** move `PackageConsumerTests.cs:576-827`'s embedded C# strings to fixture `.cs` files, excluded from compilation and copied to output.
- **JSON comparison:** `JsonElement.DeepEquals` instead of `GetRawText()` equality (`SqliteLedgerStoreTests:773-774`, `AgentCommandObservationTests:289`).
- **`ProjectBoundaryTests`:**
  - Replace its 56 source-text assertions with NetArchTest rules over assemblies.
  - Delete `:190-205`, which duplicates the exported-API comparison.
  - Delete the implementation-detail pins (`:209` `_builder.AddSwitch`, `:70-107` generator source strings).
  - Keep the project-reference XML check.
  - The API-manifest replacement itself is W2-4.

#### X3 — Examples (D10, fixes BUG-12)
NEW

- **Getting started:** add `examples/getting-started/csharp` to `Tandem.slnx` with project references, and fix its README (the `typescript/` dir has moved to tandem-ts).
- **Code-writer ledger path:** the code-writer example's ledger goes in a temp or app-data path, not the current directory.

#### X4 — CI (D6) and scripts
NEW

- **`check.yml`:** on push and PR, run `task check` plus the bridge tests, with the dotnet and node versions from `global.json`/`package.json`. Make `release.yml`/`publish.yml` depend on it.
- **Tag handling:** fix `${GITHUB_REF_NAME#v}` (a no-op, because tags match `0.*`).
- **Packing:** use `dotnet pack Tandem.slnx` with `IsPackable=false` on non-packages, instead of the hand-written 7-project loop repeated in `PackageConsumerTests:29-39`.
- **Runtime asset list:** derive `scripts/runtime-assets.mjs`'s 60-entry DLL allowlist from `*.deps.json` runtime targets, and merge it into `stage-runtime.mjs`. The owner's WIP diff already shows the list drifting. Start only after that WIP is committed.

---

### Wave 2 (serial, cross-lane)

- **W2-1 — One definition of shared types.**
  - Public Advanced `ToolEffect`/`ToolEvidence`/`ToolInvocationStatus`/`ToolResultEvidence` (`AdvancedPipeline.cs:128-173`) copy Core internals (`AgentImplementation.cs:96-126`), with 6 conversion switches (`AdvancedPipeline.cs:697-740,868-876`, `StructuredOutput.cs:364-411`). Define each once as public and used internally, and delete the switches.
  - Replace the string `PipelineActionAttempted.Effect`/`PipelineActionCompleted.Result` with those enums, and remove the Terminal's re-parsing (`TerminalRenderer.cs:773`).
  - `IPipelineAcceptanceUnitOfWork` (`AdvancedPipeline.cs:8-36`) duplicates the internal one (`PipelineObservationRuntime.cs:346`); expose one.
- **W2-2 — Run finishing and observer composition.**
  - One run-finishing helper in `Tandem.Ledger`, composed by the bridge and the terminal runner, replacing the three differing copies (`SqlitePipelineRunnerExtensions.cs:37-101`, `RegisteredGraphBridge.cs:165-290`, `TerminalPipelineRunner.cs:77-127`).
  - One public `PipelineObservers.Compose(...)` in Core, replacing the 5 composites (`SqlitePipelineRunnerExtensions.cs:104`, `TerminalPipelineRunner.cs:133-176`, `RegisteredObservationObserver.cs:117-170`, `examples/csharp-hosting/ExampleHost.cs:259`) and the test copy.
  - Unify the four `PipelineObservation` switch mappings where practical (`PipelineJournal.cs:51-170`, `RegisteredObservationObserver.Project :29-114`).
- **W2-3 — Ledger tools out of Core.** `AgentBlock.cs:1329-1383` builds `read_ledger`/`search_ledger` inline, which is Ledger behaviour in Core. Core can't reference Ledger, so Core exposes a narrow "additional agent tools" registration (or accepts `AITool`s through the existing agent options). `Tandem.Ledger` supplies the tools built on L4's presentation type.
- **W2-4 — Public API tooling (D4).**
  - Adopt `PublicApiAnalyzers` in every packable project and generate the Shipped files from the current surface.
  - Delete `PublicApiBoundaryTests`' reflection renderer, the `TANDEM_UPDATE_PUBLIC_API` switch, the 6 `ExportedApi.txt` and the 6 `PublicApiMembers.txt`.
  - Keep one test asserting no `Microsoft.Agents` types in the public surface. This replaces the 3 copies: `ProjectBoundaryTests.cs:298-432`, `PublicApiBoundaryTests.cs:317-476`, `PublicRuntimeTests.cs:288-320`.
- **W2-5 — Bridge ↔ Core validation.** After B1, confirm every range rule lives once in Core and the bridge only surfaces it. Delete any remaining duplicates.
- **W2-6 — Callback protocol and bridge rename (D5, D11; fixes BUG-13).** This is a coordinated change across tandem and tandem-ts.
  - **.NET:** `RunRegisteredGraphAsync` returns an envelope `{status:"succeeded"|"cancelled"|"contract"|"faulted", runId, state?, summary?, boundary?, problems?, message?}` (the `CallbackResult` shape, `CallbackDispatcher.cs:94-101`, already exists in the other direction), mapped in one catch block. Only real bridge bugs throw. Delete the marker (`RegisteredGraphBridge.cs:311-322,233-244`) and `FindCallbackContractException`.
  - **Rename:** `Tandem.NodeApiSpike*` becomes `Tandem.Bridge*`.
  - **tandem-ts** (separate repo; work on a branch there): consume the envelope. Delete the marker scraping and cancellation regex (`src/index.ts:133-172,1508-1516`), update the runtime loader for the rename, re-stage both runtime bundles, run its full gate, and record the release step for the owner. The tandem-ts spec's deferred G1 is closed by this.
- **W2-8 — Cadence adaptation (D1 consequence).** `~/Sites/cadence/src/Cadence.Host/Program.cs:262` uses `ReopenRunAsync` (operator-instruction resume, `tests/Cadence.Tests/OperatorInstructionResumeTests.cs`). On a `slop/tandem-cleanup` branch in `~/Sites/cadence`: move Cadence to the new Tandem surface via project references to the integration build or a local pack. Replace resume-by-reopen with a fresh run that carries the prior accepted state forward from the journal (application-owned, per "active runs are process-owned"), or remove the feature if that's the honest outcome. Record the product decision for the owner. Also adapt any other removed-API uses (`LocalProcess`, `PacketFile` overloads). Gate: Cadence's own build and tests. **Do not merge**; leave the branch for the owner.
- **W2-7 — Final sweep.**
  - Delete remaining tracker/ticket comments.
  - After K1/K3, split `AgentBlock.cs` so no file is over about 900 LOC.
  - Re-run `task check`, the bridge tests, the tandem-ts gate against the staged runtime, and Plumb.
  - Check the Cadence build against the new surface (`dotnet build` in `~/Sites/cadence` using project references or a local pack), and record follow-ups.
  - Final ledger totals.

## 7. Explicitly not doing

- **LibGit2Sharp:** the git CLI is justified (SHA-256 repos, worktrees, sparse index, user config, no native deps).
- **CliWrap for `LocalProcess`:** it has no byte-capped capture without a custom target, and `LocalProcess` already kills the process tree.
- **Replacing the OpenAI SDK or Microsoft.Extensions.AI usage:** already correct, as is YamlDotNet for packet YAML.
- **Replacing MAF `AddFanOutEdge`/`AddFanInBarrierEdge`, `Parallel.ForEachAsync`, `CreateLinkedTokenSource`+`CancelAfter`, `AsBuilder().Use` tool middleware, `AgentSkillsProvider`:** already platform features.
- **The Spectre scroll view:** Spectre has none, so manual scrolling stays.

## 8. Subagent brief template

> You are implementing work package **<WP-ID>** from `/Users/max/Sites/tandem/docs/plans/slop-cleanup.md`. First read §1 (especially §1.1 invariants and §1.2 external consumers), §2, §3, §4 and your WP in full, plus `README.md`, `CONTRIBUTING.md`, `AGENTS.md` and `CLAUDE.md`.
>
> - Work in your own git worktree on `slop/<lane>`, branched from the branch named in §6.
> - Only edit the files your lane owns. Record everything else as a follow-up.
> - Bugs are red-first. Run GitNexus `impact` before editing symbols.
> - Gate with `task check` (plus the bridge tests where relevant) and Plumb. Commit once per WP on your branch.
> - Report: files changed, LOC delta, tests added and removed (with reasons), grep evidence for deletions, Cadence/tandem-ts impact, GitNexus risk, and follow-ups. Append a row to the §9 ledger.

## 9. Ledger

| WP | Status | LOC Δ | Tests (+/−) | External impact | Impact risk | Notes / follow-ups |
|---|---|---|---|---|---|---|
| Prerequisite: owner WIP committed | todo | | | | | |
| W0-1 Bugs 1–6 | done | src/bridge +134/−87 (net +47); tests +213/−27 | +10 test cases (BUG-1 ×2 bridge, BUG-2 ×1, BUG-3 ×4 RID theory, BUG-5 ×4 theory incl. control, BUG-6 ×1); 0 removed; 2 tests rewritten to one correction (`JsonOutput_MalformedThenInvalid…`, `InvalidProofreaderResponse…`) | tandem-ts: `inspectAccepted` JSON shape unchanged, missing-run error still matches `/does not exist/`; full tandem-ts suite 150/150 against a freshly published osx-arm64 bundle. Cadence: `TerminalizingAsync` callers now see `AggregateException` if terminalization fails on a faulted/cancelled run. | `SqliteLedgerStore` class HIGH (62 upstream; member added only), `AgentBlock.ExecuteAsync` MEDIUM (7 direct), others LOW | BUG-3: eager DLL loading kept, with its reason recorded in code — node-api-dotnet resolves assemblies by calling into JS, which fails off Node's thread (0x80131509; proven: removing it failed 60+ tandem-ts tests). linux-x64 verified in Docker (amd64 under Rosetta): bundle loads and `inspectAcceptedAsync` reads a ledger; persist runs segfault in the container for both baseline and W0 bundles, so native Linux runtime verification is still needed (X4 CI). BUG-4 red shown with Debug outputs absent under `-c Release`. Follow-up (Lane X): `task check` fails on a fresh clone because `format:check` runs before the generator is built. |
| W0-2 Dead code sweep | done | src/bridge +29/−598 (net −569); manifests −18; tests +84/−194 | −1 test (`Tandem_HasNoDeliveryAssumptionsOrParallelRouteModel`: every name it guarded is gone); rewritten: `ReusedAgentDefinition_…` (real agent + unparseable client instead of the test-only `AgentOperation(Func)` ctor), 3 grep tests lost their `SearchDiagnostics` traversal assertions (content assertions kept; `…AvoidUnrelatedTraversal` renamed `…MatchOnlyTheirPrefix`), terminal tests lost assertions on removed snapshot members | Grep evidence (src, bridge, bridge-tests, tests, examples, ~/Sites/cadence/{src,tests}, ~/Sites/tandem-ts/src): every deleted symbol had 0 uses outside its definition, manifests and the listed tests. Cadence uses the public `PipelineOperationContext.ObserveCommandOutputAsync` (kept); only the internal static `PipelineOperation.ObserveCommandOutputAsync` was removed. tandem-ts: none. | CRITICAL: `DelegatePipelineNodeDescriptor` (3 direct/11 flows; dropped the ignored context parameter), `TerminalSnapshot`/`Snapshot` (19 direct; record shape). Rest LOW. detect_changes: critical (breadth), all expected | Not deleted: the `Microsoft.Agents.AI.Workflows.Checkpointing` using in `WorkflowRunner.cs` is used (`TypeId`); the spec was wrong. Cascade deletions proven by grep: `AgentBuilder.ConfigureMessageFromContext`, `AgentBlockConfig.ContextUserMessage`, `SequentialObserver`, the always-true `requireRunning` append flag, `TerminalModel` summary/token fields. Follow-up (A5): grep traversal pruning and first-page early stop are no longer mechanically asserted; cover them when the walk moves to `FileSystemEnumerable`/`git ls-files`. |
| W0-3 Repo hygiene | done (partial) | `.gitignore` +1 | none | none | n/a (no symbols) | `local-nupkgs/` now ignored (verified with `git status`). Deferred to owner (main checkout, which this work must not touch): `rm -rf samples ledger.sqlite3` in `/Users/max/Sites/tandem` — `samples/` holds only `obj/` leftovers and `ledger.sqlite3` is empty; both are already git-ignored, so nothing to commit. No `CONTRIBUTING.md` change: D7 was resolved in code (one correction), matching `CONTRIBUTING.md:143`. Wave 0 gate: `task check` green after each WP; Plumb `[]` (no findings). |
| L1 Journal store (D1) | done | src/bridge .cs +117/−959 (net −842); manifests +11/−59; tests +234/−505 | −5 tests (`Entries_CanBeReadIncrementallyAfterSequence`, `EntryIdentity_IsUniqueAcrossAWholeRun`, `Documents_UseCompareAndSwapWithIdempotentReplay`, `StorageNames_RejectAnotherContractKindNameOrVersion`, `JournalCompletionAndTerminalStatus_CommitAtomically`: each exercised only removed durability); `AnyPersistedRun_CanReopenWithReadableFacts` (6) → `TerminalRun_KeepsItsJournalReadable` (5); `UnknownSchemaVersion…` → theory over v1 and v99 (+1: old files are refused); the idempotent-replay journal tests now assert single-append content; process worker appends through the public observer; `LocalCapabilityTests.JournalFailureAtCapabilityBoundary…` → `ObserverFailureAtCapabilityBoundary…` (no ledger rollback left to assert) | **Cadence** (grep `~/Sites/cadence/{src,tests}`): uses `ReopenRunAsync` (`Program.cs:262`, `OperatorInstructionResumeTests`), `ForRun(..).ReadAsync(PipelineJournal.Stream)` + `PipelineJournal.IsAccepted` (`Program.cs:315-320`, use `ReadAcceptedAsync`/`ReadJournalAsync`); still-public members it uses: ctor, `CreateObserverAsync`, `ReadLatestAcceptedAsync`, `GetRunAsync`, `WithRunLedger`, `LedgerRunStatus` (incl. `Interrupted`), `LedgerDataException` → W2-8. **tandem-ts**: none (`inspectAccepted` → `ReadAcceptedAsync`, unchanged JSON). Every other removed symbol: 0 uses outside Ledger, its tests, bridge-tests and the manifests. | CRITICAL `SqliteLedgerStore` (22 direct, 7 flows), HIGH `LedgerStream` ctor (20 direct, tests) and `CreateRunAsync` (25 direct, tests); `ReopenRunAsync`/documents/`PipelineJournal`/`RunLedger` LOW. detect_changes: critical (breadth), 22 flows, all Ledger/bridge/test | Fresh schema v2 (`runs` + one `journal` table, JSON `record` with `json_valid` check, per-run `sequence`, no `payload_hash`/entry IDs, so L2's hash item is done here). Found by the separate-process test: first-time initialisation read `user_version` outside the write lock (masked before by `CREATE TABLE IF NOT EXISTS`); the version read now shares the `BEGIN IMMEDIATE` transaction. Cross-lane touches forced by the removal (minimal): `bridge/RegisteredGraphBridge.cs` drops `LedgerAcceptanceUnitOfWork` (it forwarded to the removed `ExecuteAsync`), `bridge-tests/InspectAcceptedTests.cs` writes through the observer, `LedgerEntryPageTests`/`LocalCapabilityTests` call sites. Follow-ups: (Lane A/W2-1) `IPipelineAcceptanceUnitOfWork`/`WithAcceptanceUnitOfWork` now have no implementation in tandem or Cadence — delete the seam; (W2-3) Core's `PipelineLedgerEntry.Stream/EntryId` are now constant/derived. Incident: `git stash` is shared by all worktrees; another lane's concurrent stash swapped working trees between lane-l and lane-a — both restored intact (patches kept in `~/Sites/slop-rescue/`). Don't use `git stash` in lane worktrees. |
| L2 Data access and locking (D2) | done | src .cs +404/−580 (net −176; `SqliteLedgerStore.cs` 1,642 → 762 across L1–L2); manifests +1/−14; tests +25/−19 | `LockContentionRetriesBoundedlyThenFails` → `Append_WaitsForAnotherWriterAndFollowsItsCommit` (a writer blocked by an open `BEGIN IMMEDIATE` waits in SQLite's busy handler and commits after it with the next sequence); WAL-reader test uses the plain store. Concurrency tests unchanged and green: 24 in-process stores, 8 separate worker processes, WAL snapshot reader, concurrent observers on one run. | Removed `SqliteLedgerOptions` and the `timeProvider`/`serializerOptions`/`options` ctor parameters: 0 callers pass them in tandem, Cadence or tandem-ts. Cadence unaffected otherwise. **tandem-ts/bridge bundles:** `Meridian.Tandem.Ledger` now depends on `Dapper.dll`, which `scripts/runtime-assets.mjs`' allowlist doesn't list — see notes. | `SqliteLedgerStore` CRITICAL (class; 22 direct), `SqlitePipelineObserver` MEDIUM (8 direct). detect_changes: critical (breadth), 17 flows, all Ledger | Dapper `QueryAsync<Row>` over one `SelectJournal` projection replaces the three hand-mapped read loops; one `ReadLatestAcceptedCoreAsync` serves both overloads; one `WithWriteTransactionAsync` (`BEGIN IMMEDIATE`) serves create/complete/append. Append is one conditional `INSERT … SELECT … WHERE EXISTS (running run) RETURNING sequence`. Deleted `RetryLockedAsync`, the second retry loop, `IsLocked`, per-connection PRAGMAs and the no-op `synchronous = FULL` (it applied only to the init connection; the SQLite default is already FULL). Busy handling is Microsoft.Data.Sqlite's (`Default Timeout=5`), plus `Foreign Keys=True` in the connection string; the `Pooling=false` comment now states the real reason. Initialisation runs once per store and retries only after a failure. Reads use a read-only connection that checks the schema version. The observer's `SemaphoreSlim` is gone. `payload_hash` was already dropped in L1. **Follow-up (owner WIP, X4):** add `Dapper.dll` to `scripts/runtime-assets.mjs` before staging bridge bundles, or derive the list from `*.deps.json` (X4); without it tandem-ts fails to load the ledger. |
| L3 Queries in SQL (BUG-11) | done | src .cs +78/−86 (net −8; `PipelineJournal.cs` → `SqlitePipelineObserver.cs`, `PipelineJournal.IsAccepted` deleted); tests +26/−0 | +1 `AgentLedgerReader_SearchMatchesValuesNotPropertyNames` — red before the fix (`search_ledger("valueType")` matched every record), green after; also covers nested payload keys vs values and command output text | none (internal queries; `ReadAcceptedAsync`/`ReadLatestAcceptedAsync` signatures unchanged; bridge tests green) | LOW: `ReadPageAsync` (2 direct), `FindActionEntryAsync` (1), `ReadAcceptedAsync`/`ReadLatestAcceptedAsync` (0 in-graph; external: bridge, Cadence) | Virtual generated columns `kind`, `step_id`, `identity`, `value_type`, `payload_type` and two derived ones, `accepted` and `agent_readable`, now the single definition of "accepted value" and "agent-readable record" (were C# `IsAccepted`/`IsReadableCommand`); indexes `(run_id, accepted, step_id, value_type)` and `(run_id, kind, step_id, identity)`. Latest-accepted, accepted list, action lookup and pages filter in SQL (`ORDER BY id DESC LIMIT 1`, page `LIMIT limit+1`); the full-scan TODO is resolved. Search: `json_tree(record)` atoms with `instr(lower(..))` — values only; case folding is now ASCII-only (recorded in CHANGELOG). Schema stays version 2 (unreleased; defined once across L1–L3). |
