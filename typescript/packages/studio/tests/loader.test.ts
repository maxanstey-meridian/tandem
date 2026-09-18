import assert from "node:assert/strict";
import { mkdtemp, writeFile, readFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, resolve } from "node:path";
import test from "node:test";
import { pathToFileURL, fileURLToPath } from "node:url";
import { discoverConfig } from "../src/discovery.js";
import { loadPipeline } from "../src/loader.js";
const sdk = pathToFileURL(
  resolve(fileURLToPath(new URL("../../sdk/dist/index.js", import.meta.url))),
).href;
const zod = pathToFileURL(
  resolve(fileURLToPath(new URL("../../../node_modules/zod/index.js", import.meta.url))),
).href;
async function fixture(source: string) {
  const root = await mkdtemp(join(tmpdir(), "studio-loader-")),
    config = join(root, "tandem.config.ts");
  await writeFile(config, source.replaceAll("SDK", sdk).replaceAll("ZOD", zod));
  return { root, config };
}
test("constructs and inspects without executing any lifecycle callback or exposing clients", async () => {
  const touched = join(tmpdir(), `studio-touch-${crypto.randomUUID()}`);
  const { root, config } = await fixture(
    `import {pipeline,stage,interaction,agent,capability,parallel,output,route} from "SDK";import {z} from "ZOD";import {appendFileSync} from "node:fs";console.log("application stdout");console.error("application stderr");const touched=${JSON.stringify("TOUCH")};const called=()=>{appendFileSync(touched,"called");throw new Error("executed")};const begin=stage({id:"begin",execute:called});const ask=interaction({id:"ask",persist:false,requestSchema:z.object({q:z.string()}),responseSchema:z.object({a:z.string()}),request:called,apply:called});const tool=capability({name:"lookup",instructions:"look up",schema:z.object({query:z.string()}),apply:called,summarize:called});const worker=agent({id:"worker",persist:true,instructions:"work",client:{kind:"openai-compatible",version:1,endpoint:"https://secret.invalid",model:"secret-model",wireApi:"responses"},message:called,capabilities:[tool],output:{instructions:"decide",schema:z.object({decision:z.string()}),apply:called}});const left=stage({id:"left",persist:true,execute:called});const right=stage({id:"right",persist:false,execute:called});const both=parallel({id:"both",persist:false,branches:{left,right},merge:called});const done=output({id:"done",persist:true,summary:called});const failed=output({id:"failed",failed:true,persist:false,summary:called});export const tandem={createPipeline:()=>pipeline({name:"real",state:z.object({value:z.string()}),nodes:[begin,ask,worker,both,done,failed],start:begin,routes:[route({from:begin,to:ask,label:"ask",when:called}),route({from:ask,to:worker,label:"work"}),route({from:worker,to:both,label:"worked",outcome:"success"}),route({from:worker,to:failed,label:"failed",outcome:"failed"}),route({from:both,to:done,label:"merged",outcome:"success"})],outputs:[done,failed]})};`.replace(
      "TOUCH",
      touched,
    ),
  );
  const result = await loadPipeline(config);
  assert.equal(result.ok, true);
  if (!result.ok) {
    return;
  }
  assert.deepEqual(
    result.graph.nodes.map((n) => n.kind),
    ["stage", "interaction", "agent", "parallel", "completion", "failure"],
  );
  assert.equal(result.graph.nodes[0]?.persist, undefined);
  assert.equal(result.graph.nodes[1]?.persist, false);
  assert.equal(result.graph.nodes[2]?.persist, true);
  assert.equal(result.graph.nodes[2]?.agent?.capabilities[0]?.name, "lookup");
  assert.deepEqual(result.graph.nodes[2]?.agent?.capabilities[0]?.requestSchema, {
    $schema: "https://json-schema.org/draft/2020-12/schema",
    type: "object",
    properties: { query: { type: "string" } },
    required: ["query"],
  });
  assert.deepEqual(result.graph.nodes[2]?.agent?.outputSchema, {
    $schema: "https://json-schema.org/draft/2020-12/schema",
    type: "object",
    properties: { decision: { type: "string" } },
    required: ["decision"],
  });
  assert.equal(result.graph.nodes[3]?.persist, false);
  assert.equal(result.graph.nodes[4]?.persist, true);
  assert.equal(result.graph.nodes[5]?.persist, false);
  assert.deepEqual(
    result.graph.nodes[3]?.branches?.map((branch) => branch.participant.persist),
    [true, false],
  );
  assert.equal(result.graph.routes[0]?.conditional, true);
  assert.equal(result.graph.routes[2]?.outcome, "success");
  assert.equal(JSON.stringify(result.graph).includes("secret"), false);
  await assert.rejects(readFile(touched));
});
test("terminates a loader child even when application construction leaves an active handle", async () => {
  const pidFile = join(tmpdir(), `studio-loader-pid-${crypto.randomUUID()}`);
  const { config } = await fixture(
    `import {pipeline,stage,output,route} from "SDK";import {z} from "ZOD";import {writeFileSync} from "node:fs";writeFileSync(${JSON.stringify(pidFile)},String(process.pid));setInterval(()=>{},60_000);const begin=stage({id:"begin",execute:s=>s});const done=output({id:"done",summary:()=>"done"});export const tandem={createPipeline:()=>pipeline({name:"handle",state:z.object({}),nodes:[begin,done],start:begin,routes:[route({from:begin,to:done,label:"done"})],outputs:[done]})};`,
  );
  const result = await loadPipeline(config);
  assert.equal(result.ok, true);
  const pid = Number(await readFile(pidFile, "utf8"));
  assert.throws(() => process.kill(pid, 0), /ESRCH/);
});
test("terminates and diagnoses a config import that exceeds the loading bound", async () => {
  const { config } = await fixture(
    "while (true) {} export const tandem = { createPipeline: () => ({}) };",
  );
  const started = Date.now();

  const result = await loadPipeline(config, { timeoutMs: 150 });

  assert.equal(result.ok, false);
  if (!result.ok) {
    assert.match(result.error, /exceeded 150ms.*blocking work/);
  }
  assert.ok(Date.now() - started < 2_000);
});

test("discovers and loads the checked-in debate config without running it", async () => {
  const workspace = resolve(
    fileURLToPath(new URL("../../../../examples/debate/typescript", import.meta.url)),
  );
  const config = await discoverConfig(join(workspace, "src"));
  assert.equal(config, join(workspace, "tandem.config.ts"));

  const result = await loadPipeline(config);

  assert.equal(result.ok, true);
  if (result.ok) {
    assert.equal(result.graph.name, "debate");
    assert.equal(result.graph.start, "open");
    assert.deepEqual(result.graph.outputs, ["complete", "debate-failed"]);
    assert.equal(JSON.stringify(result.graph).includes("openrouter.ai"), false);
    assert.equal(JSON.stringify(result.graph).includes("OPENROUTER_API_KEY"), false);
  }
});
test("isolates missing exports, thrown imports, async factories, and construction failures", async () => {
  for (const [source, expected] of [
    [`export const other={};`, `must export`],
    [`throw new Error("import boom")`, `import boom`],
    [`export const tandem={createPipeline:async()=>({})};`, `synchronous`],
    [
      `import {pipeline,stage,output,route} from "SDK";import {z} from "ZOD";const a=stage({id:"same",execute:s=>s});const b=output({id:"same",summary:()=>""});export const tandem={createPipeline:()=>pipeline({name:"bad",state:z.object({}),nodes:[a,b],start:a,routes:[route({from:a,to:b,label:"done"})],outputs:[b]})};`,
      `unique`,
    ],
  ] as const) {
    const { config } = await fixture(source);
    const result = await loadPipeline(config);
    assert.equal(result.ok, false);
    if (!result.ok) {
      assert.match(result.error, new RegExp(expected));
    }
  }
});
