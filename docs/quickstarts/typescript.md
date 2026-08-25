# TypeScript Quickstart

## Requirements

- macOS on Apple silicon
- Node.js 22 or newer
- .NET 10 runtime

## Create the application

```sh
mkdir tandem-quickstart
cd tandem-quickstart
npm init -y
npm install @maxanstey-meridian/tandem zod
```

Create `index.mjs`:

```js
import { output, pipeline, route, run, stage } from "@maxanstey-meridian/tandem";
import { z } from "zod";

const State = z.object({ value: z.string() });
const normalize = stage({
  id: "normalize",
  execute: (state) => ({ ...state, value: state.value.trim().toLowerCase() }),
});
const done = output({ id: "done", summary: (state) => state.value });
const normalizeInput = pipeline({
  name: "normalize-input",
  state: State,
  nodes: [normalize, done],
  start: normalize,
  routes: [route({ from: normalize, to: done, label: "normalized" })],
  outputs: [done],
});

const result = await run(normalizeInput, { value: "  Hello Tandem  " });
console.log(result.summary);
```

Run it:

```sh
node index.mjs
```

Application code installs only `@maxanstey-meridian/tandem`; platform runtime packages are selected
automatically. Continue with the package-backed
[getting-started progression](../../examples/getting-started).
