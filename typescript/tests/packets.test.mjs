import {
  PacketFileError,
  parsePacketFile,
  readPacketFile,
} from "@maxanstey-meridian/tandem-packets";
import assert from "node:assert/strict";
import { readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { z } from "zod";

const Packet = z
  .object({
    title: z.string().min(1),
    repository: z.string(),
    outcomes: z.array(z.object({ id: z.string(), description: z.string() })),
    verification: z.array(z.string()),
    constraints: z.array(z.string()).default([]),
    mode: z.enum(["normal", "strict"]),
    note: z.string().nullable().default(null),
  })
  .strict();
const fixtures = resolve(import.meta.dirname, "../../tests/packet-fixtures");

test("consumes the shared portable fixture manifest", async () => {
  const manifest = JSON.parse(await readFile(join(fixtures, "manifest.json"), "utf8"));
  for (const fixture of manifest) {
    if (fixture.valid) {
      await assert.doesNotReject(
        readPacketFile(join(fixtures, fixture.file), Packet),
        fixture.file,
      );
    } else {
      await assert.rejects(
        readPacketFile(join(fixtures, fixture.file), Packet),
        (error) =>
          error instanceof PacketFileError &&
          error.problems.some(({ path }) => path === fixture.path),
        fixture.file,
      );
    }
  }

  const input = await readPacketFile(join(fixtures, "valid-nested.md"), Packet);
  assert.deepEqual(
    input.value.outcomes.map(({ id }) => id),
    ["registration"],
  );
  assert.equal(input.value.mode, "strict");
  assert.equal(input.context, "Inspect authentication.\n\n---\nThis is Markdown.");
  assert.equal(input.source.resolvePath(input.value.repository), resolve(fixtures, "my-app"));
});

test("normalizes BOM and line endings and supports Zod defaults and transforms", () => {
  const schema = z
    .object({
      title: z.string().transform((value) => value.toUpperCase()),
      values: z.array(z.string()).default([]),
    })
    .strict();
  const input = parsePacketFile("\uFEFF---\r\ntitle: packet\r\n---\r\n\r\nBody\r\n", schema, {
    sourceName: "memory",
  });
  assert.deepEqual(input.value, { title: "PACKET", values: [] });
  assert.equal(input.context, "Body");
  assert.throws(() => input.source.resolvePath("relative"), PacketFileError);
});

test("normalizes Zod issues to stable paths", () => {
  assert.throws(
    () =>
      parsePacketFile(
        "---\noutcomes:\n  - id: 1\nextra: true\n---",
        z.object({ outcomes: z.array(z.object({ id: z.string() })) }).strict(),
      ),
    (error) =>
      error instanceof PacketFileError &&
      error.problems.some(({ path }) => path === "$.outcomes[0].id") &&
      error.problems.some(({ path }) => path === "$"),
  );
});

for (const [name, source, message] of [
  ["missing opening delimiter", "title: no", "must start"],
  ["missing closing delimiter", "---\ntitle: no", "requires a closing"],
  ["empty frontmatter", "---\n\n---", "nonempty YAML mapping"],
  ["non-mapping root", "---\n- value\n---", "must be a YAML mapping"],
  ["duplicate keys", "---\ntitle: one\ntitle: two\n---", "Map keys must be unique"],
  ["custom tags", "---\ntitle: !custom value\n---", "custom tags"],
  ["anchors", "---\ntitle: &title value\n---", "aliases and anchors"],
]) {
  test(`rejects ${name}`, () =>
    assert.throws(
      () => parsePacketFile(source, z.object({ title: z.string() }).strict()),
      (error) => error instanceof PacketFileError && error.message.includes(message),
    ));
}

test("bounds source bytes and nesting", () => {
  assert.throws(
    () =>
      parsePacketFile(
        `---\nvalue: ${"x".repeat(1024 * 1024)}\n---`,
        z.object({ value: z.string() }),
      ),
    /byte limit/,
  );
  const nested = Array.from({ length: 66 }, (_, index) => `${"  ".repeat(index)}value:`).join("\n");
  assert.throws(
    () => parsePacketFile(`---\n${nested}\n${"  ".repeat(66)}leaf\n---`, z.unknown()),
    /nesting limit/,
  );
});

test("reports source-aware I/O errors and honors cancellation", async () => {
  const path = join(tmpdir(), "missing-tandem-packet.md");
  await assert.rejects(
    readPacketFile(path, Packet),
    (error) =>
      error instanceof PacketFileError && error.sourceName === path && error.cause instanceof Error,
  );
  const controller = new AbortController();
  controller.abort();
  await assert.rejects(readPacketFile(path, Packet, { signal: controller.signal }), {
    name: "AbortError",
  });
});

test("preserves cancellation raised during file I/O", async () => {
  const path = join(tmpdir(), `cancelled-tandem-packet-${process.pid}.md`);
  const content = `---\ntitle: Packet\nrepository: .\noutcomes: []\nverification: []\nmode: normal\n---\n${"x".repeat(512 * 1024)}`;
  try {
    await writeFile(path, content);
    const controller = new AbortController();
    const reading = readPacketFile(path, Packet, { signal: controller.signal });
    controller.abort();
    await assert.rejects(reading, { name: "AbortError" });
  } finally {
    await rm(path, { force: true });
  }
});

test("reports the location of nested custom tags", () => {
  assert.throws(
    () =>
      parsePacketFile(
        "---\ntitle: Packet\nmetadata:\n  value: !custom tagged\n---",
        z.object({ title: z.string(), metadata: z.object({ value: z.string() }) }).strict(),
      ),
    (error) =>
      error instanceof PacketFileError &&
      error.problems.some(({ path }) => path === "$.metadata.value"),
  );
});

test("rejects oversized files before parsing", async () => {
  const path = join(tmpdir(), `oversized-tandem-packet-${process.pid}.md`);
  try {
    await writeFile(path, "x".repeat(1024 * 1024 + 1));
    await assert.rejects(readPacketFile(path, Packet), /byte limit/);
  } finally {
    await rm(path, { force: true });
  }
});
