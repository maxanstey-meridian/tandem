# @maxanstey-meridian/tandem

The TypeScript authoring API for [Tandem](https://github.com/maxanstey-meridian/tandem), a typed
agentic pipeline SDK running on .NET and Microsoft Agent Framework.

## Requirements

- macOS on Apple silicon
- Node.js 22 or newer
- .NET 10 runtime

## Install

```sh
npm install @maxanstey-meridian/tandem zod
```

Install only this package in application code. Its runtime packages are selected automatically.

## Quick Start

```ts
import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";
import { z } from "zod";

const State = z.object({
  input: z.string(),
  normalized: z.string().nullable(),
});
type State = z.infer<typeof State>;

const normalize = stage<State>({
  id: "normalize",
  execute: (state) => ({
    ...state,
    normalized: state.input.trim().toLowerCase(),
  }),
});

const done = output<State>({
  id: "done",
  summary: (state) => state.normalized!,
});

const normalizeInput = pipeline({
  name: "normalize-input",
  state: State,
  nodes: [normalize, done],
  start: normalize,
  routes: [route({ from: normalize, to: done, label: "normalized" })],
  outputs: [done],
});

const result = await run(normalizeInput, { input: "  Hello  ", normalized: null });
console.log(result.state.normalized);
```

State holds application facts, participants perform work, and routes decide what runs next. See the
[TypeScript guide](https://github.com/maxanstey-meridian/tandem/tree/main/typescript) for agents,
capabilities, interactions, persistence, and complete examples.

Licensed under the [MIT License](./LICENSE).
