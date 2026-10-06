# Packet Files

A packet file is Markdown with YAML frontmatter. It's a convenient, human-editable way to hand a
pipeline its input:

```markdown
---
title: Add slug support
requirements:
  - Lower-case the input
  - Replace spaces with hyphens
---

Any context the agents should read goes in the body.
```

The packet packages decode that file at your application's boundary. You own the packet type,
its validation, and the explicit conversion into state. Reading a packet doesn't configure or
start a pipeline.

```text
packet file -> validated application packet -> application state -> pipeline run
```

::: code-group

```csharp [C#]
// dotnet add package Meridian.Tandem.Packets
using Tandem.Packets;

var input = await PacketFile.ReadAsync<WorkPacket>(path);
var state = WorkState.Create(input.Value, input.Context, input.Source);
```

```ts [TypeScript]
// npm install @maxanstey-meridian/tandem-packets zod
import { readPacketFile } from "@maxanstey-meridian/tandem-packets";

const input = await readPacketFile(path, WorkPacket.strict());
const state = createWorkState(input.value, input.context, input.source);
```

:::

`Context` is the Markdown body. `Source` lets you resolve a value relative to the packet file,
with `input.Source.ResolvePath(...)` in C#, when your application decides one of its values is a
path.

## The profile

Both languages enforce the same portable profile:

- Plain scalars resolve with the YAML 1.2 core schema. Empty values are null, only
  `true`/`false` (in lower, title or upper case) are booleans, and `.inf` and `.nan` are
  rejected.
- Aliases, anchors, merge keys, custom tags, multiple YAML documents and duplicate keys are
  rejected.
- Files are limited to 1 MiB, and nesting to 64 levels.

C# maps `snake_case` YAML names onto your type, rejects unknown fields, and accepts an optional
FluentValidation `IValidator<T>` as `validator`. In TypeScript, your Zod schema owns shape and
validation. Unknown fields are rejected only when you pass a strict schema, such as
`z.object({...}).strict()`.
