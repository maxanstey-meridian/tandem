import assert from "node:assert/strict";
import { execFile } from "node:child_process";
import test from "node:test";
import { promisify } from "node:util";
const exec = promisify(execFile);
for (const enabled of [false, true]) {
  test(`ledger logging retains accepted values with model tools ${enabled ? "enabled" : "disabled by default"}`, async () => {
    const { stdout } = await exec(
      process.execPath,
      [
        new URL("ledger-tools-child.mjs", import.meta.url).pathname,
        enabled ? "enabled" : "default",
      ],
      { timeout: 20000 },
    );
    const result = JSON.parse(stdout.trim());
    assert.equal(result.succeeded, true);
    assert.equal(result.persisted, true);
    assert.deepEqual(
      result.tools.sort(),
      enabled ? ["read_ledger", "read_ledger_entry", "search_ledger"] : [],
    );
  });
}
