# Meridian.Tandem.Advanced

Advanced execution-policy and workspace APIs for Tandem pipelines.

```sh
dotnet add package Meridian.Tandem.Advanced --version 0.1.0-alpha.1
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
`nextCharacterOffset`, omitting `startLine` when resuming mid-line.

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
