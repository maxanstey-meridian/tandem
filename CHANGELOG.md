# Changelog

## Unreleased — breaking

- Structured-output recovery allows exactly one corrective response, as `CONTRIBUTING.md` documents; the code previously allowed two.
- Registered workspace tools and workspace commands named `copy_file`, `move_file` or `create_directory` are now rejected as collisions with the built-in workspace tools.
- `RunWithTerminalAsync` raises an `AggregateException` holding both failures when a faulted or cancelled run's `TerminalizingAsync` callback also fails; it previously swallowed the terminalization failure.
- Removed unused Advanced structured-output APIs: `StructuredOutputPolicy`, `StructuredJsonExtractor`, `StructuredOutputAcceptancePolicies`, `AgentBuilder.WithStructuredOutput(...)`, `AgentBuilder.WithMessageFromContext(...)` and `AdvancedAgentMessage<TState>`. Use `WithOutput`/`WithJsonOutput`/`WithRawOutput` and `WithMessage`.
- Removed `RunLedger.ReadRecentAsync(...)`.
- `StreamRetryChatClient` is the only retry layer: it now also retries HTTP 408, 429 and 5xx responses (`ClientResultException`). Build the inner OpenAI client with `RetryPolicy = new ClientRetryPolicy(0)`; with the SDK default, each attempt was measured to become four requests.
- Removed `AgentMessageOutcome`, an exact copy of `OperationOutcome`. `AgentMessageContext<TState>.LatestOutcome` and the `AgentConversationPolicy<TState>` outcome parameter are now `OperationOutcome`.
- Workspace grep respects `.gitignore` inside a Git work tree: it searches `git ls-files --cached --others --exclude-standard`, so ignored files are skipped and tracked files in directories such as `bin/` are now searched. The built-in prune list applies only outside Git or below an explicitly named ignored path. `includeExcluded` walks the file system without pruning, as before. Globs now use `Microsoft.Extensions.FileSystemGlobbing`: `*` and `**` behave as before, but `?` is matched literally. Unreadable directories are skipped silently instead of being listed in `skipped`.
- Fixed workspace commands (`AgentCommand`) run through MAF's `LocalShellExecutor`, the same executor as `run_shell`, instead of a login shell (`/bin/zsh -lc` on macOS, `/bin/bash -lc` on Linux). Login-profile setup such as `PATH` additions is no longer applied; commands that depended on it must use absolute paths or set up their environment explicitly. The tool result is MAF's `ShellResult`, whose captured stdout and stderr are newline-terminated.

## Unreleased — fixes and additions

- `SqliteLedgerStore.ReadAcceptedAsync(runId)` reads a run's accepted journal values on a read-only connection, filtered to the runtime journal and in insert order. The bridge's `InspectAcceptedAsync` uses it instead of its own SQL, so other streams' rows are no longer read as journal records.
- The Node bridge resolves the bundled SQLite native library by runtime identifier (`libe_sqlite3.so` on Linux) instead of always looking for `libe_sqlite3.dylib`.
- MAF's workspace write, replace and delete tools resolve every path through the workspace path authority, so reads and deletes through symbolic links or reparse points are rejected as writes already were.
- Read-only Git tools resolve path arguments through the workspace path authority: `.git` segments are rejected case-insensitively and paths through symbolic links are rejected, as the file tools already did.
- `git_blame` returns a text page with `offset`/`limit`/`nextOffset` like the other paged Git tools; it previously cut porcelain output at 500 lines with no continuation.
