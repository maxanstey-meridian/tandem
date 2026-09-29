# Changelog

## Unreleased — breaking

- Structured-output recovery allows exactly one corrective response, as `CONTRIBUTING.md` documents; the code previously allowed two.
- Registered workspace tools and workspace commands named `copy_file`, `move_file` or `create_directory` are now rejected as collisions with the built-in workspace tools.
- `RunWithTerminalAsync` raises an `AggregateException` holding both failures when a faulted or cancelled run's `TerminalizingAsync` callback also fails; it previously swallowed the terminalization failure.
- Removed unused Advanced structured-output APIs: `StructuredOutputPolicy`, `StructuredJsonExtractor`, `StructuredOutputAcceptancePolicies`, `AgentBuilder.WithStructuredOutput(...)`, `AgentBuilder.WithMessageFromContext(...)` and `AdvancedAgentMessage<TState>`. Use `WithOutput`/`WithJsonOutput`/`WithRawOutput` and `WithMessage`.
- Removed `RunLedger.ReadRecentAsync(...)`.
- The ledger is now an append-only run-history journal (runs are process-owned; the ledger records them for inspection). Removed the general-purpose durability surface: `LedgerStream<T>`, `LedgerDocument<T>`, `LedgerDocumentValue<T>`, `AcceptedLedgerEntry<T>`, `PipelineJournal`, `RunLedger.AppendAsync/ReadAsync/ReadAfterAsync/ReadDocumentAsync/WriteDocumentAsync`, `SqliteLedgerStore.ReopenRunAsync(...)` (runs can no longer be resumed), `SqliteLedgerStore.ExecuteAsync(...)` (ambient transactions), the `ledger_contracts` registry, optimistic document concurrency and idempotent replay of entry IDs. `SqliteLedgerStore.CreateRunAsync(...)` is no longer public; `CreateObserverAsync(...)` creates the run. Read a run's journal with the new `SqliteLedgerStore.ReadJournalAsync(runId)` (`LedgerJournalEntry`), its accepted values with `ReadAcceptedAsync`/`ReadLatestAcceptedAsync`.
- The Node bridge no longer wraps capability acceptance in a ledger transaction; the journal records acceptance as history.
- `SqliteLedgerStore` takes only the database path: removed `SqliteLedgerOptions` (busy timeout and the hand-written lock-retry loop; SQLite's own busy handling now waits up to 5 seconds for a locked database) and the unused `timeProvider` and `serializerOptions` parameters. The store initialises its schema once per instance.
- Ledger reads (`GetRunAsync`, `ReadJournalAsync`, `ReadAcceptedAsync`, `ReadLatestAcceptedAsync` and the ledger tools) open the database read-only: they no longer create a missing ledger file, and they refuse a file with another schema version. `ReadLatestAcceptedAsync` throws `KeyNotFoundException` for an unknown run instead of returning `null`, like `ReadAcceptedAsync`.
- `Meridian.Tandem.Ledger` depends on Dapper.
- `search_ledger` case-insensitive matching folds ASCII letters only (SQLite `lower()`); it previously used .NET ordinal-ignore-case.
- **On-disk incompatibility:** the ledger schema is now version 2 (one `journal` table; no `run_entries`, `run_documents` or `ledger_contracts`). Ledger files written by earlier versions (schema version 1) are refused with "Ledger schema version '1' is not supported; expected '2'". There is no migration; start a new ledger file.

## Unreleased — fixes and additions

- `SqliteLedgerStore.ReadAcceptedAsync(runId)` reads a run's accepted journal values on a read-only connection, filtered to the runtime journal and in insert order. The bridge's `InspectAcceptedAsync` uses it instead of its own SQL, so other streams' rows are no longer read as journal records.
- The Node bridge resolves the bundled SQLite native library by runtime identifier (`libe_sqlite3.so` on Linux) instead of always looking for `libe_sqlite3.dylib`.
- `search_ledger` matches JSON values only; it no longer matches property names such as `stepId` or `valueType`, which appear in every record. Ledger filters (accepted values, agent-readable records, action lookup, search) run in SQL over indexed generated columns instead of loading and deserialising every journal row.
