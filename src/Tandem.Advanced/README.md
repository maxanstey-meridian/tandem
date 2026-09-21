# Meridian.Tandem.Advanced

Advanced execution-policy and workspace APIs for Tandem pipelines.

```sh
dotnet add package Meridian.Tandem.Advanced --version 0.1.0
```

Use this package only when an application deliberately participates in runtime mechanics such as
Harness workspace authority, output-acceptance policy, or invocation context. Ordinary pipeline
state, participants, capabilities, interactions, and routes belong to `Meridian.Tandem`.

See the [Tandem repository](https://github.com/maxanstey-meridian/tandem) for usage and examples.

`UseHarness(instructions, maxContextWindowTokens, maxOutputTokens, disableCompaction)`
configures context and output limits independently of lifecycle checkpoint capabilities.
The limits also appear in runtime usage observations. `WithCheckpoint` remains optional.

Workspace reads use source line numbers (`startLine`, `lineCount`). Responses contain numbered
`lines`; an oversized line can span fragments. Continue with the returned `nextStartLine` and
`nextCharacterOffset` as `startLine` and `characterOffset` together when resuming mid-line.

Directory listings, Git status and grep paginate with plain integer `offset`/`limit`, returning
`nextOffset`; grep's `limit` counts matching records, not characters. Grep defaults
to case-insensitive regex matching and pruning common build/dependency directories. Use `literal`,
`caseSensitive` and `includeExcluded` to change those choices. Explicit path prefixes can select
normally excluded directories, but never bypass workspace, Git-metadata or symlink restrictions.
Skipped files and oversized matches are reported; source reads retrieve long matching lines.
Queries do not promise snapshots across repository edits: restart after edits.

Named commands capture up to 16 MiB per output stream using Tandem's process runner. The model
receives bounded stdout/stderr previews. When ledger tools are enabled and the action is persisted,
`diagnostics.entryCursor` identifies captured output: use `read_ledger_entry` with that cursor and
`stream: "stdout"` or `"stderr"`, following `nextOffset`. `captureTruncated` means the hard capture
ceiling was reached; `previewTruncated` only means more captured output is available. Without a
durable reference the response explicitly says retrieval is unavailable. Acceptance-policy process
evidence is also bounded and marks truncation; full diagnostics remain in the ledger record.

Raw response parsing is an Advanced extension: import `Tandem.Advanced`, implement
`IAgentRawOutputDefinition<TState, TOutput>`, and call `WithRawOutput(definition, apply)`.
Instructions are sent on the initial turn; rejection requests a corrected response in the same
format without enabling a provider JSON response format. Intrinsic validation precedes contextual
validation and acceptance. A parser throws `InvalidOperationException` for expected rejection;
cancellation and other unexpected exceptions propagate. Adapters can supply `valueType` to preserve
the authored identity in accepted observations and ledger records.

`AgentCommand.Define(name, description, command)` creates a parameterless fixed command.
The overload taking `arguments` enables optional model-supplied arguments and publishes that list
as an example argument array in the tool schema. Examples are guidance, not fixed appended values
or an allowlist. The model may supply up to 16 strings of at most 200 characters each. Values are
shell-quoted individually, but program flags can still change program behavior: grant this authority
only where intended. Use the fixed command text for application-controlled flags and defaults.

Ledger retrieval is opt-in through the run ledger configuration; the bridge uses
`enableLedgerTools`. Retrieval remains limited to the current run and readable persisted records.

### Migration from 0.1.0

`PacketCommand` and `PacketValidator` have been removed. Use `AgentCommand.Define` directly;
application-owned packet formats should validate their own transport shape and use that same
command admission API. This is a breaking change for consumers of those two Advanced types.
The raw-output API added after 0.1.0 is now in `Tandem.Advanced` rather than Core.
