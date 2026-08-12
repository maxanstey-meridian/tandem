import { readFile, stat } from "node:fs/promises";
import { dirname, isAbsolute, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { isAlias, isMap, isNode, isPair, isScalar, isSeq, parseDocument } from "yaml";
import type { ParsedNode } from "yaml";
import type { z } from "zod";

const maximumSourceBytes = 1024 * 1024;
const maximumNestingDepth = 64;

export type PacketFile<T> = {
  readonly value: T;
  readonly context: string;
  readonly source: PacketSource;
};
export type PacketSource = {
  readonly name?: string;
  readonly fullPath?: string;
  readonly directory?: string;
  resolvePath(path: string): string;
};
export type PacketProblem = {
  readonly path: string;
  readonly message: string;
  readonly line?: number;
  readonly column?: number;
};

export class PacketFileError extends Error {
  readonly sourceName?: string;
  readonly problems: readonly PacketProblem[];
  constructor(
    message: string,
    options: {
      readonly sourceName?: string;
      readonly problems: readonly PacketProblem[];
      readonly cause?: unknown;
    },
  ) {
    super(message, { cause: options.cause });
    this.name = "PacketFileError";
    this.sourceName = options.sourceName;
    this.problems = options.problems;
  }
}

export function parsePacketFile<TSchema extends z.ZodType>(
  content: string,
  schema: TSchema,
  options: { readonly sourceName?: string } = {},
): PacketFile<z.output<TSchema>> {
  const source = sourceFromName(options.sourceName);
  const sourceName = source.name;
  if (Buffer.byteLength(content, "utf8") > maximumSourceBytes) {
    throw failure(sourceName, `Packet source exceeds the ${maximumSourceBytes} byte limit.`, "$");
  }
  const lines = content
    .replace(/^\uFEFF/, "")
    .replace(/\r\n?/g, "\n")
    .split("\n");
  if (lines[0] !== "---") {
    throw failure(sourceName, "Packet frontmatter must start with '---'.", "$", 1, 1);
  }
  const closing = lines.indexOf("---", 1);
  if (closing < 0) {
    throw failure(sourceName, "Packet frontmatter requires a closing '---'.", "$");
  }
  const frontmatter = lines.slice(1, closing).join("\n");
  if (frontmatter.trim() === "") {
    throw failure(sourceName, "Packet frontmatter must be a nonempty YAML mapping.", "$");
  }

  let document;
  try {
    document = parseDocument(frontmatter, {
      customTags: [],
      prettyErrors: false,
      schema: "core",
      uniqueKeys: true,
    });
  } catch (cause) {
    throw failure(
      sourceName,
      "Packet frontmatter contains invalid YAML.",
      "$",
      undefined,
      undefined,
      cause,
    );
  }
  if (document.errors.length > 0) {
    const problems = document.errors.map((error) => ({
      path: "$",
      message: error.message,
      ...(error.linePos?.[0] === undefined
        ? {}
        : { line: error.linePos[0].line + 1, column: error.linePos[0].col }),
    }));
    throw new PacketFileError(formatError(sourceName, problems), {
      sourceName,
      problems,
      cause: document.errors[0],
    });
  }
  if (document.contents === null || !isMap(document.contents)) {
    throw failure(sourceName, "Packet frontmatter must be a YAML mapping.", "$");
  }
  validateNode(document.contents, 0, sourceName);
  const decoded = document.toJS({ maxAliasCount: 0 });
  const result = schema.safeParse(decoded);
  if (!result.success) {
    const problems = result.error.issues.map((issue) => ({
      path: zodPath(issue.path),
      message: issue.message,
    }));
    throw new PacketFileError(formatError(sourceName, problems), {
      sourceName,
      problems,
      cause: result.error,
    });
  }
  return {
    value: result.data,
    context: lines
      .slice(closing + 1)
      .join("\n")
      .trim(),
    source,
  };
}

export async function readPacketFile<TSchema extends z.ZodType>(
  path: string | URL,
  schema: TSchema,
  options: { readonly signal?: AbortSignal } = {},
): Promise<PacketFile<z.output<TSchema>>> {
  const fullPath = normalize(resolve(path instanceof URL ? fileURLToPath(path) : path));
  options.signal?.throwIfAborted();
  let content: string;
  try {
    const source = await stat(fullPath);
    if (source.size > maximumSourceBytes) {
      throw failure(fullPath, `Packet source exceeds the ${maximumSourceBytes} byte limit.`, "$");
    }
    content = await readFile(fullPath, { encoding: "utf8", signal: options.signal });
  } catch (cause) {
    if (cause instanceof PacketFileError) {
      throw cause;
    }
    if (cause instanceof Error && cause.name === "AbortError") {
      throw cause;
    }
    throw failure(fullPath, "Packet file could not be read.", "$", undefined, undefined, cause);
  }
  return parsePacketFile(content, schema, { sourceName: fullPath });
}

function sourceFromName(name?: string): PacketSource {
  const fullPath = name !== undefined && isAbsolute(name) ? normalize(name) : undefined;
  const directory = fullPath === undefined ? undefined : dirname(fullPath);
  return {
    ...(name === undefined ? {} : { name }),
    ...(fullPath === undefined ? {} : { fullPath, directory }),
    resolvePath(path) {
      if (isAbsolute(path)) {
        return normalize(path);
      }
      if (directory === undefined) {
        throw failure(name, "A relative path requires a packet filesystem source.", "$");
      }
      return resolve(directory, path);
    },
  };
}

function validateNode(node: ParsedNode, depth: number, sourceName?: string, path = "$"): void {
  if (depth > maximumNestingDepth) {
    throw failure(
      sourceName,
      `Packet frontmatter exceeds the ${maximumNestingDepth} level nesting limit.`,
      path,
    );
  }
  if (isAlias(node) || node.anchor !== undefined) {
    throw failure(sourceName, "YAML aliases and anchors are not supported.", "$");
  }
  if (node.tag !== undefined && !node.tag.startsWith("tag:yaml.org,2002:")) {
    throw failure(sourceName, "YAML custom tags are not supported.", path);
  }
  if (isMap(node)) {
    for (const item of node.items) {
      if (isPair(item)) {
        if (isNode(item.key) && isScalar(item.key) && item.key.value === "<<") {
          throw failure(sourceName, "YAML merge keys are not supported.", path);
        }
        if (
          !isScalar(item.key) ||
          typeof item.key.value !== "string" ||
          item.key.value.length === 0
        ) {
          throw failure(sourceName, "Mapping keys must be nonempty strings.", path);
        }
        validateNode(item.key, depth + 1, sourceName, path);
        const childPath = path === "$" ? `$.${item.key.value}` : `${path}.${item.key.value}`;
        if (isNode(item.value)) {
          validateNode(item.value, depth + 1, sourceName, childPath);
        }
      }
    }
  }
  if (isSeq(node)) {
    for (const [index, item] of node.items.entries()) {
      if (isNode(item)) {
        validateNode(item, depth + 1, sourceName, `${path}[${index}]`);
      }
    }
  }
  if (isScalar(node) && typeof node.value === "number" && !Number.isFinite(node.value)) {
    throw failure(sourceName, "YAML non-finite numbers are not supported.", path);
  }
}

function failure(
  sourceName: string | undefined,
  message: string,
  path: string,
  line?: number,
  column?: number,
  cause?: unknown,
): PacketFileError {
  const problem = {
    path,
    message,
    ...(line === undefined ? {} : { line }),
    ...(column === undefined ? {} : { column }),
  };
  return new PacketFileError(formatError(sourceName, [problem]), {
    sourceName,
    problems: [problem],
    cause,
  });
}
function zodPath(path: readonly PropertyKey[]): string {
  if (path.length === 0) {
    return "$";
  }
  return path.reduce<string>(
    (value, part) =>
      typeof part === "number"
        ? `${value}[${part}]`
        : value === ""
          ? String(part)
          : `${value}.${String(part)}`,
    "$",
  );
}
function formatError(sourceName: string | undefined, problems: readonly PacketProblem[]): string {
  return `${sourceName === undefined ? "Packet file" : `Packet file '${sourceName}'`} is invalid: ${problems.map((problem) => `${problem.path}: ${problem.message}`).join("; ")}`;
}
