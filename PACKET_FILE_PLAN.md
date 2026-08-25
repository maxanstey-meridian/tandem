# Typed Packet File Plan

## Status

Implemented in `Tandem.Packets` and `@maxanstey-meridian/tandem-packets`, with one manifest-backed portable
fixture suite consumed by both languages. Cadence migration remains deliberately out of
scope for this implementation pass.

## Product decision

Tandem should provide one conventional way to turn a human-authored execution brief
into typed application input:

```text
Markdown file
  + strict YAML frontmatter shaped by the application's domain schema
  + Markdown context for the agents
  -> typed packet file
  -> application state
  -> pipeline run
```

The ordinary API should express the user's goal:

```csharp
var input = PacketFile.Read<Packet>(path);
```

```typescript
const input = await readPacketFile(path, Packet);
```

Applications define packet meaning. Tandem owns file reading, the Markdown/frontmatter
envelope, strict YAML decoding, source-aware errors, and structural validation. The
application owns semantic validation, source-relative domain interpretation, state
creation, and pipeline execution.

This follows the existing Tandem boundary:

```text
Application meaning in application types.
Commodity parsing in an optional Tandem package.
Accepted facts in application state.
Lifecycle decisions in routes.
Runtime mechanics below the seam.
```

## Why this belongs in Tandem

Without a shared packet-file seam, every Tandem application must independently choose
and implement:

- a brief format;
- Markdown/frontmatter splitting;
- YAML behavior;
- typed decoding and validation;
- parser diagnostics;
- source-path handling; and
- authoring/schema support.

Those are common input mechanics. Repository paths, Git bases, coding outcomes, shell
verification commands, and publication remain Cadence concepts.

Tandem therefore supplies a bare convention and typed mechanism. Cadence becomes one
specialization rather than the source of a universal coding-packet schema.

## Package boundary

Add optional packages:

```text
Tandem.Packets
@maxanstey-meridian/tandem-packets
```

They are pure input packages, not runtime packages.

`Tandem.Packets`:

- targets `net10.0`;
- does not depend on MAF, `Tandem.Advanced`, Ledger, Terminal, or hosting;
- depends on YamlDotNet and the minimum validation abstractions required by the final
  C# API;
- exposes no YamlDotNet types.

`@maxanstey-meridian/tandem-packets`:

- is ESM and requires Node 22+, matching Tandem's TypeScript packages;
- does not depend on `@maxanstey-meridian/tandem`, `@maxanstey-meridian/tandem-runtime`, or the native bridge;
- uses `yaml` as its maintained YAML implementation;
- accepts caller-owned Zod schemas and declares Zod as a peer dependency;
- exposes no parser-library AST types.

Core Tandem consumers must not acquire YAML dependencies. Installing a packet package
is an explicit application choice.

## Domain-first model

The application's type is authoritative.

### C#

```csharp
public sealed record Packet(
    string Title,
    string Repository,
    string Base,
    IReadOnlyList<PacketOutcome> Outcomes,
    IReadOnlyList<string> Verification,
    IReadOnlyList<string> Constraints
);

public sealed record PacketOutcome(string Id, string Description);
```

### TypeScript

```typescript
import { z } from "zod";

const PacketOutcome = z.object({
  id: z.string().min(1),
  description: z.string().min(1),
});

const Packet = z.object({
  title: z.string().min(1),
  repository: z.string().min(1),
  base: z.string().min(1),
  outcomes: z.array(PacketOutcome).min(1),
  verification: z.array(z.string().min(1)).min(1),
  constraints: z.array(z.string()).default([]),
});
```

Tandem does not define universal outcome, verification, repository, or constraint
fields. Those fields appear here because Cadence defines them.

## Packet file contract

A packet file is UTF-8 Markdown with one YAML frontmatter mapping:

```markdown
---
title: Implement registration
repository: ./my-app
base: main
outcomes:
  - id: registration
    description: Users can register with a valid email and password
verification:
  - dotnet test
constraints: []
---

Inspect the existing authentication flow before choosing the change surface.
```

The frontmatter maps to the application-provided type or schema. The Markdown body is
returned separately as context. It is not injected into the domain value through an
attribute, binder, or naming convention.

```text
frontmatter -> Value
body        -> Context
file path   -> Source
```

### Envelope rules

- Input is decoded as UTF-8.
- A UTF-8 BOM is accepted and removed.
- Line endings are normalized to `\n` before envelope parsing.
- The first content line must be exactly `---`.
- A closing line exactly equal to `---` is required.
- Frontmatter must contain one nonempty YAML mapping.
- YAML multi-document input is rejected.
- The body may be empty.
- `Context` preserves interior Markdown and trims outer whitespace.
- Delimiter-like text after the closing delimiter is ordinary Markdown.
- File extension is not significant.

### Portable YAML profile

C# and TypeScript must accept and reject the same shared fixtures.

- Field names are exact after each language's ordinary property-name convention:
  `PascalCase` C# properties map to `snake_case`; TypeScript schema keys are authored in
  their packet spelling.
- Root metadata must be a mapping.
- Mappings, sequences, strings, booleans, finite numbers, and null are supported.
- Mapping order and sequence order are preserved.
- Duplicate mapping keys are rejected.
- C# rejects unknown fields. TypeScript rejects them when the caller-owned Zod schema is
  strict; the packet package does not alter schema policy.
- Explicit YAML custom tags are rejected.
- Aliases, anchors, and merge keys are rejected in the ordinary profile.
- Non-finite numeric values are rejected.
- YAML timestamps remain strings unless the application schema explicitly converts
  them.
- Packet sources are limited to 1 MiB and YAML nesting to 64 levels. Aliases remain
  disabled rather than expanded.

The implementation must prove this profile through language-neutral fixtures rather
than assuming YamlDotNet and `yaml` have identical defaults. Parser defaults are never
the public contract.

### Structural and semantic validity

Packet reading proves:

- the envelope is valid;
- YAML matches the application's requested shape;
- required members and value types can construct that shape;
- no unknown or duplicate fields exist; and
- application validation supplied to the read operation succeeds.

Packet reading does not prove external facts such as:

- a path exists;
- a directory is a Git repository;
- a Git reference resolves;
- a verification command is safe or sufficient; or
- an outcome is meaningfully observable.

Those checks belong to the application or an explicit preflight operation. Reading a
packet must not execute Git, create a run, mutate files, or contact a model.

## C# public API

### Ordinary use

```csharp
using Tandem.Packets;

PacketFile<Packet> input = PacketFile.Read<Packet>(path);

Packet packet = input.Value;
string context = input.Context;
PacketSource source = input.Source;
```

`PacketFile.Read<T>` is the conventional entry point:

- Markdown plus strict YAML frontmatter;
- underscored YAML names;
- unknown and duplicate fields rejected;
- immutable records and `IReadOnlyList<T>` supported;
- normalized context;
- absolute source path and directory returned;
- source-aware `PacketFileException` failures.

### Semantic validation

Avoid hidden service lookup, validator-name discovery, or global registration. Validation
is explicit when needed:

```csharp
var input = PacketFile.Read(path, new PacketValidator());
```

Proposed overloads:

```csharp
public static class PacketFile
{
    public static PacketFile<T> Parse<T>(string content, string? sourceName = null);

    public static PacketFile<T> Parse<T>(
        string content,
        IValidator<T> validator,
        string? sourceName = null
    );

    public static PacketFile<T> Read<T>(string path);

    public static PacketFile<T> Read<T>(string path, IValidator<T> validator);

    public static ValueTask<PacketFile<T>> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken = default
    );

    public static ValueTask<PacketFile<T>> ReadAsync<T>(
        string path,
        IValidator<T> validator,
        CancellationToken cancellationToken = default
    );
}
```

Type inference makes validated use concise:

```csharp
var input = PacketFile.Read(path, new PacketValidator());
```

The parser handles structure; FluentValidation handles domain meaning. Expected invalid
input throws one source-aware packet exception containing structured problems. File I/O
and parser exceptions are retained as `InnerException` where useful.

### Result and errors

```csharp
public sealed record PacketFile<T>(
    T Value,
    string Context,
    PacketSource Source
);

public sealed record PacketSource(
    string? Name,
    string? FullPath,
    string? Directory
)
{
    public string ResolvePath(string path);
}

public sealed record PacketProblem(
    string Path,
    string Message,
    int? Line = null,
    int? Column = null
);

public sealed class PacketFileException : Exception
{
    public string? SourceName { get; }
    public IReadOnlyList<PacketProblem> Problems { get; }
}
```

Exact constructor/member shape must be reviewed through Tandem's exported API manifests
during implementation. Parser-specific exception and node types must not escape.

### C# binding feasibility gate

Before implementation is accepted, a focused spike must prove the maintained codec can
construct:

- positional records;
- nested positional records;
- `IReadOnlyList<string>` from populated and empty YAML sequences;
- omitted optional collections with C# defaults;
- nullable members;
- enum members;
- required members;
- unknown-field rejection; and
- duplicate-key rejection.

If direct YamlDotNet object binding cannot satisfy immutable records reliably, the
implementation may normalize the supported YAML profile to plain CLR values and use
`System.Text.Json` for the final typed construction. That is internal machinery and must
not alter the public API or portable fixtures. Do not require mutable YAML DTOs from
applications.

## TypeScript public API

### Ordinary use

```typescript
import { readPacketFile } from "@maxanstey-meridian/tandem-packets";

const input = await readPacketFile(path, Packet);

input.value; // z.output<typeof Packet>
input.context;
input.source;
```

Proposed API:

```typescript
import type { z } from "zod";

export type PacketFile<T> = {
  readonly value: T;
  readonly context: string;
  readonly source: PacketSource;
};

export type PacketSource = {
  readonly name?: string;
  readonly fullPath?: string;
  readonly directory?: string;
  resolvePath(path: string): string;
};

export function parsePacketFile<TSchema extends z.ZodType>(
  content: string,
  schema: TSchema,
  options?: { readonly sourceName?: string },
): PacketFile<z.output<TSchema>>;

export function readPacketFile<TSchema extends z.ZodType>(
  path: string | URL,
  schema: TSchema,
  options?: { readonly signal?: AbortSignal },
): Promise<PacketFile<z.output<TSchema>>>;
```

Zod owns TypeScript structural and semantic validation. Defaults, refinements, and
explicit transforms are supported because packet ingestion is not a lossless bridge
boundary. The return type is `z.output<TSchema>`.

Packet values are not automatically required to be Tandem-state JSON. The SDK's existing
state boundary remains authoritative if the application later places packet values in
pipeline state.

### TypeScript errors

```typescript
export type PacketProblem = {
  readonly path: string;
  readonly message: string;
  readonly line?: number;
  readonly column?: number;
};

export class PacketFileError extends Error {
  readonly sourceName?: string;
  readonly problems: readonly PacketProblem[];
}
```

One public error type keeps normal handling simple. Problems distinguish envelope,
syntax, shape, and application-validation failures through stable messages and paths;
the parser/filesystem error remains available as `cause` where useful.

## Source-relative application values

Tandem returns source information but does not guess which strings are paths.

Cadence resolves its repository explicitly when creating its domain packet:

```csharp
var input = PacketFile.Read(path, new PacketValidator());
var packet = input.Value with
{
    Repository = input.Source.ResolvePath(input.Value.Repository),
};
```

This line is application meaning, not parser plumbing. It avoids path-binding attributes,
property selectors, and hidden filesystem behavior while keeping packet-relative paths
easy to use.

TypeScript applications use the same shape:

```typescript
const input = await readPacketFile(path, Packet);
const repository = input.source.resolvePath(input.value.repository);
```

`ResolvePath` returns rooted paths unchanged after normalization and resolves relative
paths against the source directory. It fails clearly when parsing from a string without
a filesystem source.

## State and pipeline integration

Packet files are run input, not pipeline configuration and not pipeline state themselves.

### C#

```csharp
var input = PacketFile.Read(path, new PacketValidator());
var state = CadenceState.Create(Packet.From(input));
var result = await runner.RunAsync(cadence, state, cancellationToken: cancellationToken);
```

### TypeScript

```typescript
const input = await readPacketFile(path, Packet);
const state = createCadenceState(input);
const result = await run(cadence, state);
```

Do not add `Pipeline.Start<T>(path)`, `RunPacketAsync`, or implicit state construction.
Those APIs would conflate reusable graph configuration, external input, application
state, and execution.

## Authoring and schema support

Schema/example generation is valuable, but it must not delay the read/parse contract or
be falsely promised as fully derivable from arbitrary validators.

Phase one exposes machine-readable structural description only where the native schema
system can do so honestly:

- TypeScript applications may use Zod's JSON Schema facilities directly.
- C# does not claim that arbitrary FluentValidation rules can be converted to JSON
  Schema.
- Tandem ships checked-in packet format documentation and fixtures.

A later `Describe`/`Example` feature may be added when one real application needs it and
the source of truth is clear. Do not reflect example values or semantic authoring advice
from domain types.

Application skills remain responsible for judgement:

- outcomes should be independently observable;
- verification should come from real repository tooling;
- constraints should be task-specific;
- context should bound useful initial inspection; and
- unresolved product or policy decisions should route to a Human.

Cadence should ship its own packet-authoring skill after the packet API lands. Tandem may
ship a small generic packet-file reference, but must not encode Cadence semantics.

## Implementation phases

### Phase 1: freeze the portable contract

1. Add language-neutral fixtures under `tests/packet-fixtures/`.
2. Cover valid nested records, empty and omitted collections, context normalization,
   CRLF, BOM, exact ordering, and source-relative path resolution.
3. Cover malformed envelopes, invalid YAML, duplicate keys, unknown fields, aliases,
   custom tags, multi-document YAML, non-mapping roots, type mismatch, and excessive
   nesting/source size.
4. Record expected normalized values and problem paths in fixture metadata.
5. Verify the profile against YamlDotNet and `yaml` before exposing public packages.

Status: complete. Both implementations consume `tests/packet-fixtures/manifest.json` and
agree on its valid fixtures and expected invalid paths.

### Phase 2: implement `Tandem.Packets`

1. Add `src/Tandem.Packets/Tandem.Packets.csproj` and include it in `Tandem.slnx`.
2. Implement envelope parsing, source normalization, bounded reading, YAML normalization,
   typed construction, FluentValidation integration, and problem translation.
3. Keep parser configuration immutable and internal.
4. Add `ExportedApi.txt` and `PublicApiMembers.txt`.
5. Extend `PublicApiBoundaryTests` for the new assembly and prohibit YamlDotNet types on
   public signatures.
6. Add focused unit tests and external-consumer tests.
7. Pack and restore `Tandem.Packets` in the package-consumer proof; assert existing core
   consumers still do not receive YamlDotNet.

Status: complete, including public API, package-consumer, analyzer, build, and full
repository gates.

### Phase 3: implement `@maxanstey-meridian/tandem-packets`

1. Add `typescript/packages/packets` as an ESM package.
2. Implement pure `parsePacketFile` and asynchronous `readPacketFile`.
3. Use the same fixtures and normalize Zod issues to stable packet paths.
4. Add positive and negative type tests proving `z.output` inference.
5. Add cancellation, file I/O, parser-policy, and resource-bound tests.
6. Extend TypeScript build, lint, format, test, and clean scripts for the package.
7. Extend the packed-consumer test to install `@maxanstey-meridian/tandem-packets` without any Tandem native
   runtime package and execute a real parse/read.

Status: implemented, including shared fixtures and packed-consumer coverage.

### Phase 4: migrate Cadence

1. Add a `Tandem.Packets` package reference and update the local pack script/feed.
2. Define a Cadence `PacketValidator` for file-boundary semantic validation while keeping
   `CadenceState.Create` guards for programmatic callers.
3. Replace `YamlPacketReader` with `PacketFile.Read(path, validator)` plus the one
   Cadence-owned repository resolution step.
4. Preserve Cadence's exact list order and command strings.
5. Remove Cadence's direct YamlDotNet dependency and YAML transport DTOs.
6. Add CLI boundary tests for missing/malformed packets, packet-relative repositories,
   invalid verification, error output, exit code, and no run-directory side effects.
7. Update Cadence README with the exact packet contract and body semantics.
8. Add a Cadence packet-authoring skill and validate every checked-in example through the
   production reader.

Migration compatibility requirements:

- relative packet paths resolve against the current working directory;
- relative repositories resolve against the packet directory;
- required scalar values retain Cadence's trim behavior;
- outcome and verification order remain exact;
- duplicate verification commands remain distinct by index;
- verification command and constraint text is not silently rewritten;
- implementation context keeps normalized `\n` line endings and outer trimming;
- invalid packets still fail before configuration loading or run-directory creation; and
- workspace preparation, not packet parsing, proves that `base` resolves in Git.

Exit criterion: Cadence's full `task check` passes against freshly packed Tandem packages,
and its direct dependency graph no longer contains application-owned YAML plumbing.

Status: complete. Cadence reads its domain `Packet` through `Tandem.Packets`, resolves
its repository explicitly, preserves authored command/constraint text and ordering,
ships packet authoring guidance plus a checked-in example, and passes its full repository
gate against freshly packed Tandem packages.

### Phase 5: documentation and examples

1. Add paired C#/TypeScript packet examples to Tandem README.
2. Explain that domain types define frontmatter and context is returned separately.
3. Document structural parsing versus application preflight.
4. Document supported YAML profile and limits without teaching parser internals.
5. Add one minimal sample application that reads a packet, creates state, and runs a
   pipeline only if existing samples cannot demonstrate the journey naturally.

Status: complete for the earned surface. Tandem documents paired C#/TypeScript packet
ingestion and package-level format details; Cadence provides the concrete application
example and packet-authoring skill, so a second generic sample application is unnecessary.

## Test matrix

### Shared behavior

- valid scalar, nested object, sequence, and empty sequence;
- omitted optional/defaulted collection;
- nullable values;
- order preservation;
- CRLF, CR, LF, and BOM;
- empty and multiline Markdown context;
- delimiter-like Markdown after frontmatter;
- malformed/open/empty frontmatter;
- non-mapping root;
- duplicate and unknown keys;
- aliases, anchors, merge keys, tags, and multi-document input;
- wrong scalar and collection types;
- excessive file size and nesting;
- stable problem paths and source names.

### C# specifics

- positional and property records;
- nested immutable records;
- `IReadOnlyList<T>`;
- nonnullable and required members;
- FluentValidation multiple and cross-field problems;
- synchronous and asynchronous file reads;
- cancellation;
- public API manifests;
- optional-package dependency isolation.

### TypeScript specifics

- `z.output` inference;
- strict object schemas;
- defaults, refinements, coercions, and explicit transforms;
- multiple Zod issues;
- `AbortSignal` cancellation;
- ESM package exports;
- package use without native Tandem runtime installation.

### Cadence migration

- README packet parses unchanged;
- packet-relative repository behavior;
- all current reader errors or deliberate replacement messages;
- packet failure precedes config failure;
- packet failure creates no run directory;
- every packet field reaches the same downstream consumer unchanged;
- verification tools and deterministic commands preserve exact index/order;
- Reviewer grounding remains unchanged;
- publication records the resolved repository and accepted packet title.

## Deliberate non-goals

- No generic document-definition DSL.
- No configurable codec in the ordinary API.
- No automatic format detection.
- No JSON, TOML, or custom frontmatter in phase one.
- No body-binding attributes or lambdas.
- No path-property annotations or selectors.
- No automatic mapping from packet files to `TState`.
- No automatic pipeline execution.
- No Git, shell, workspace, or publication semantics in Tandem.
- No claim that reflection or validators can generate good semantic authoring guidance.
- No backward-compatibility shim for Cadence's internal `YamlPacketReader` after migration.

If a second real consumer later requires another representation, add an Advanced/custom
codec seam from concrete evidence. Do not weaken the default packet contract preemptively.

## Risks and mitigations

### Cross-parser YAML differences

Risk: YamlDotNet and `yaml` differ on YAML versions, scalar typing, aliases, duplicates,
and constructor binding.

Mitigation: portable profile, shared fixtures, explicit parser configuration, and a
phase-one feasibility gate before public API commitment.

### False nullability guarantees in C#

Risk: runtime deserialization cannot infer every C# nullable-reference invariant as
reliably as a compiler.

Mitigation: prove constructor/required-member behavior, translate missing values into
structured problems, and recommend FluentValidation for semantic nonblank rules.

### Hidden magic

Risk: validator discovery, path conventions, or body binding could make the sleek API
surprising.

Mitigation: the default only performs format mechanics; validators are explicit; context
and source are separate; path resolution is application-authored.

### Package sprawl

Risk: two optional packages add release and test work.

Mitigation: keep each package pure and narrow. This avoids burdening every core/runtime
consumer with YAML and filesystem dependencies.

### Format becomes prematurely universal

Risk: application-specific fields leak into Tandem.

Mitigation: Tandem defines only envelope and typed decoding. All packet fields are supplied
by the consuming application's type/schema.

## Completion criteria

The feature is complete when:

- C# and TypeScript expose the proposed ordinary APIs;
- both implementations pass one shared portable fixture suite;
- core Tandem packages retain their current dependency isolation;
- immutable C# records and `IReadOnlyList<T>` work without transport DTOs;
- TypeScript result types derive from `z.output`;
- errors are source-aware and contain stable structured problems;
- Cadence has no direct YAML reader or YAML DTOs;
- Cadence reads its domain packet through Tandem and preserves current behavior;
- Tandem and Cadence READMEs explain the user journey rather than parser plumbing;
- Cadence ships semantic packet-authoring guidance; and
- full C#, TypeScript, package-consumer, Cadence, formatting, analyzer, architecture,
  and build gates pass.
