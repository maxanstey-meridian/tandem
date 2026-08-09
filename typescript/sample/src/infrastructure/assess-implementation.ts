import { spawn } from "node:child_process";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import type { VerificationResult } from "../state.js";

const cases = [
  { input: "  Hello, World!  ", expected: "hello-world" },
  { input: "Crème brûlée", expected: "creme-brulee" },
  { input: "already---slugged", expected: "already-slugged" },
  { input: "___Edge___", expected: "edge" },
  { input: "!!!", expected: "" },
  { input: "mañana café 123", expected: "manana-cafe-123" },
] as const;

const outputLimit = 64 * 1024;
const timeoutMs = 2_000;

export const assessImplementation = async (source: string): Promise<VerificationResult> => {
  const directory = await mkdtemp(join(tmpdir(), "tandem-function-assessment-"));
  try {
    return await new Promise<VerificationResult>((resolve) => {
      const child = spawn(
        process.execPath,
        [new URL("assess-implementation-worker.mjs", import.meta.url).pathname],
        { cwd: directory, env: {}, stdio: ["pipe", "pipe", "pipe"] },
      );
      const stdout: Buffer[] = [];
      const stderr: Buffer[] = [];
      let stdoutBytes = 0;
      let stderrBytes = 0;
      let settled = false;
      const finish = (result: VerificationResult) => {
        if (settled) {
          return;
        }
        settled = true;
        clearTimeout(timer);
        resolve(result);
      };
      const collect = (chunks: Buffer[], chunk: Buffer, stream: "stdout" | "stderr") => {
        const total =
          stream === "stdout" ? (stdoutBytes += chunk.length) : (stderrBytes += chunk.length);
        if (total > outputLimit) {
          child.kill("SIGKILL");
          finish({
            passed: false,
            cases: [],
            error: `Assessment ${stream} exceeded ${outputLimit} bytes.`,
          });
          return;
        }
        chunks.push(chunk);
      };
      child.stdout.on("data", (chunk: Buffer) => collect(stdout, chunk, "stdout"));
      child.stderr.on("data", (chunk: Buffer) => collect(stderr, chunk, "stderr"));
      child.once("error", (error) =>
        finish({ passed: false, cases: [], error: `Assessment failed: ${error.message}` }),
      );
      child.once("close", (code, signal) => {
        if (settled) {
          return;
        }
        const errorOutput = Buffer.concat(stderr).toString("utf8").trim();
        if (code !== 0) {
          finish({
            passed: false,
            cases: [],
            error: `Assessment exited with ${signal ?? code}${errorOutput ? `: ${errorOutput}` : ""}`,
          });
          return;
        }
        try {
          finish(JSON.parse(Buffer.concat(stdout).toString("utf8")) as VerificationResult);
        } catch (error) {
          finish({
            passed: false,
            cases: [],
            error: `Assessment returned invalid output: ${error instanceof Error ? error.message : String(error)}`,
          });
        }
      });
      const timer = setTimeout(() => {
        child.kill("SIGKILL");
        finish({ passed: false, cases: [], error: `Assessment timed out after ${timeoutMs}ms.` });
      }, timeoutMs);
      child.stdin.end(JSON.stringify({ source, cases }));
    });
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
};
