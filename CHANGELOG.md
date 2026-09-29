# Changelog

## Unreleased — breaking

- Structured-output recovery allows exactly one corrective response, as `CONTRIBUTING.md` documents; the code previously allowed two.
- Registered workspace tools and workspace commands named `copy_file`, `move_file` or `create_directory` are now rejected as collisions with the built-in workspace tools.
- `RunWithTerminalAsync` raises an `AggregateException` holding both failures when a faulted or cancelled run's `TerminalizingAsync` callback also fails; it previously swallowed the terminalization failure.
- Removed unused Advanced structured-output APIs: `StructuredOutputPolicy`, `StructuredJsonExtractor`, `StructuredOutputAcceptancePolicies`, `AgentBuilder.WithStructuredOutput(...)`, `AgentBuilder.WithMessageFromContext(...)` and `AdvancedAgentMessage<TState>`. Use `WithOutput`/`WithJsonOutput`/`WithRawOutput` and `WithMessage`.
- Removed `RunLedger.ReadRecentAsync(...)`.

## Unreleased — fixes and additions

- `SqliteLedgerStore.ReadAcceptedAsync(runId)` reads a run's accepted journal values on a read-only connection, filtered to the runtime journal and in insert order. The bridge's `InspectAcceptedAsync` uses it instead of its own SQL, so other streams' rows are no longer read as journal records.
- The Node bridge resolves the bundled SQLite native library by runtime identifier (`libe_sqlite3.so` on Linux) instead of always looking for `libe_sqlite3.dylib`.
