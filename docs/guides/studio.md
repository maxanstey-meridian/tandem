# Tandem Studio

Tandem Studio is a local viewer and route editor for a TypeScript pipeline. It loads the
pipeline your project exports and serves a Nuxt app on your machine.

```sh
npm install -D @maxanstey-meridian/tandem-studio @maxanstey-meridian/tandem
npx tandem-studio
```

## Exporting a pipeline

Studio looks for a `tandem.config.ts` in the current directory. It exports a `tandem` object
whose `createPipeline` returns the pipeline:

```ts
// tandem.config.ts
import { draftingClient, reviewingClient } from "./src/clients.js";
import { createPipeline } from "./src/pipeline.js";

export const tandem = {
  createPipeline: () =>
    createPipeline({ proposer: draftingClient, critic: reviewingClient, judge: reviewingClient }),
};
```

Point at another file with `npx tandem-studio --config path/to/tandem.config.ts`.

Loading the config builds the pipeline but doesn't run it, so Studio never calls a model. The
[debate example](https://github.com/maxanstey-meridian/tandem-ts/tree/main/examples/debate/typescript)
has a working config.

Studio needs Node.js 22 or newer, and the .NET 10 runtime for Tandem itself.
