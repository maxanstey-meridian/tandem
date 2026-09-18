import assert from "node:assert/strict";
import test from "node:test";
import { parseStudioArguments } from "../src/cli-args.js";

test("Studio CLI accepts only the optional config path", () => {
  assert.deepEqual(parseStudioArguments([]), {});
  assert.deepEqual(parseStudioArguments(["--config", "app/tandem.config.ts"]), {
    config: "app/tandem.config.ts",
  });
  assert.throws(() => parseStudioArguments(["--config"]), /requires a path|Usage/);
  assert.throws(() => parseStudioArguments(["--config", "--other"]), /requires a path/);
  assert.throws(() => parseStudioArguments(["--unknown"]), /Usage/);
  assert.throws(() => parseStudioArguments(["extra"]), /Usage/);
});
