import { z } from "zod";
import { output, pipeline, route, stage } from "../packages/sdk/dist/index.js";

const State = z.object({ value: z.number() });
const work = stage({ id: "work", execute: (state) => state });
const impostor = stage({ id: "work", execute: (state) => state });
const done = output({ id: "done", summary: () => "done" });
const errors = [];
for (const definition of [
  { nodes: [work, done], start: impostor, routes: [], outputs: [done] },
  {
    nodes: [work, done],
    start: work,
    routes: [route({ from: impostor, to: done, label: "bad" })],
    outputs: [done],
  },
  {
    nodes: [work, done],
    start: work,
    routes: [],
    outputs: [output({ id: "done", summary: () => "done" })],
  },
  {
    nodes: [work, done],
    start: work,
    routes: [
      route({ from: work, to: done, label: "first" }),
      route({ from: work, to: done, label: "second" }),
    ],
    outputs: [done],
  },
  {
    nodes: [work, done],
    start: work,
    routes: [route({ from: work, to: done, label: "done" })],
    outputs: [],
  },
  {
    nodes: [work, done],
    start: work,
    routes: [],
    outputs: [done],
  },
]) {
  try {
    pipeline({ name: "identity", state: State, ...definition });
  } catch (error) {
    errors.push(String(error));
  }
}
console.log(JSON.stringify(errors));
process.exit(0);
