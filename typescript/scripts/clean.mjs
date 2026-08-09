import { rmSync } from "node:fs";
rmSync(new URL("../packages/sdk/dist", import.meta.url), { recursive: true, force: true });
rmSync(new URL("../packages/runtime-darwin-arm64/runtime", import.meta.url), {
  recursive: true,
  force: true,
});
rmSync(new URL("../.runtime-publish", import.meta.url), { recursive: true, force: true });
rmSync(new URL("../sample/dist", import.meta.url), { recursive: true, force: true });
