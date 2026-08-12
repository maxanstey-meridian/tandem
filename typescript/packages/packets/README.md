# `@tandem/packets`

Read Markdown packet files with YAML frontmatter into an application-owned Zod schema:

```ts
import { readPacketFile } from "@tandem/packets";
import { z } from "zod";

const Packet = z.object({ title: z.string().min(1) }).strict();
const input = await readPacketFile("work.packet.md", Packet);
```

The package enforces the portable YAML and envelope profile. Zod owns the requested value shape and semantic validation. Unknown fields are rejected only when the caller supplies a strict schema, such as `z.object({...}).strict()`; `@tandem/packets` does not silently make caller schemas strict.

Packet sources are limited to 1 MiB and YAML nesting is limited to 64 levels.
