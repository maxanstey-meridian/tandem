import { inspectAcceptedAsync, runRegisteredGraphAsync } from "@tandem/runtime";
import { isDeepStrictEqual } from "node:util";
import { z } from "zod";

type Callback = (state: string, input: string) => string | Promise<string>;
const participantBrand: unique symbol = Symbol("participant");
const compileCapabilityBrand: unique symbol = Symbol("compileCapability");

export class TandemError extends Error {
  constructor(message: string, options?: ErrorOptions) {
    super(message, options);
    this.name = "TandemError";
  }
}
export class TandemRuntimeError extends TandemError {
  constructor(
    readonly operation: "run" | "inspect",
    cause: unknown,
  ) {
    super(`Tandem ${operation} failed: ${cause instanceof Error ? cause.message : String(cause)}`, {
      cause,
    });
    this.name = "TandemRuntimeError";
  }
}
export class TandemCancellationError extends TandemRuntimeError {
  constructor(cause: unknown) {
    super("run", cause);
    this.name = "AbortError";
  }
}
export class ContractValidationError extends TandemError {
  readonly problems: ReadonlyArray<{ path: string; message: string }>;
  constructor(boundary: string, problems: ReadonlyArray<{ path: string; message: string }>) {
    super(
      `${boundary} validation failed: ${problems.map((p) => `${p.path}: ${p.message}`).join("; ")}`,
    );
    this.name = "ContractValidationError";
    this.problems = problems;
  }
}

function path(parts: PropertyKey[]): string {
  return parts.length === 0
    ? "$"
    : `$${parts.map((part) => (typeof part === "number" ? `[${part}]` : `.${String(part)}`)).join("")}`;
}
function parse<T>(schema: z.ZodType<T>, value: unknown, boundary: string): T {
  let result: z.ZodSafeParseResult<T>;
  try {
    result = schema.safeParse(value);
  } catch (error) {
    if (error instanceof Error && /async/i.test(error.message)) {
      throw new ContractValidationError(boundary, [
        {
          path: "$",
          message:
            "Async Zod refinements are unsupported; Tandem contracts must validate synchronously.",
        },
      ]);
    }
    throw error;
  }
  if (!result.success) {
    throw new ContractValidationError(
      boundary,
      result.error.issues.map((issue) => ({ path: path(issue.path), message: issue.message })),
    );
  }
  if (!isDeepStrictEqual(result.data, value)) {
    throw new ContractValidationError(boundary, [
      {
        path: "$",
        message:
          "Zod contract changed the boundary value. Coercion, defaults, transforms, and stripping are unsupported.",
      },
    ]);
  }
  return result.data;
}
function parseJson<T>(schema: z.ZodType<T>, json: string, boundary: string): T {
  let value: unknown;
  try {
    value = JSON.parse(json);
  } catch {
    throw new ContractValidationError(boundary, [{ path: "$", message: "Invalid JSON" }]);
  }
  return parse(schema, value, boundary);
}
function jsonSchema<T>(schema: z.ZodType<T>, boundary: string): string {
  try {
    return JSON.stringify(z.toJSONSchema(schema));
  } catch (error) {
    throw new ContractValidationError(boundary, [
      { path: "$", message: error instanceof Error ? error.message : String(error) },
    ]);
  }
}

interface Participant<TState> {
  readonly id: string;
  readonly [participantBrand]: (state: TState) => TState;
}
export interface Stage<TState> extends Participant<TState> {
  readonly kind: "stage";
}
export interface Interaction<TState, TRequest, TResponse> extends Participant<TState> {
  readonly kind: "interaction";
  readonly requestType?: TRequest;
  readonly responseType?: TResponse;
}
export interface Agent<TState> extends Participant<TState> {
  readonly kind: "agent";
}
export interface Terminal<TState> extends Participant<TState> {
  readonly kind: "terminal";
}
type Node<TState> =
  | Stage<TState>
  | Interaction<TState, unknown, unknown>
  | Agent<TState>
  | Terminal<TState>;

abstract class NodeImplementation<TState> implements Participant<TState> {
  readonly [participantBrand]!: (state: TState) => TState;
  constructor(
    readonly id: string,
    readonly persist?: boolean,
  ) {}
}
class StageImplementation<TState> extends NodeImplementation<TState> implements Stage<TState> {
  readonly kind = "stage";
  constructor(
    id: string,
    persist: boolean | undefined,
    readonly execute: (state: TState) => TState | Promise<TState>,
  ) {
    super(id, persist);
  }
}
export function stage<TState>(definition: {
  id: string;
  execute: (state: TState) => TState | Promise<TState>;
  persist?: boolean;
}): Stage<TState> {
  return new StageImplementation(definition.id, definition.persist, definition.execute);
}

class InteractionImplementation<TState, TRequest, TResponse>
  extends NodeImplementation<TState>
  implements Interaction<TState, TRequest, TResponse>
{
  readonly kind = "interaction";
  readonly requestType?: TRequest;
  readonly responseType?: TResponse;
  constructor(
    id: string,
    persist: boolean | undefined,
    readonly requestSchema: z.ZodType<TRequest>,
    readonly responseSchema: z.ZodType<TResponse>,
    readonly request: (state: TState) => TRequest,
    readonly handle: (request: TRequest) => TResponse | Promise<TResponse>,
    readonly apply: (state: TState, response: TResponse) => TState,
  ) {
    super(id, persist);
  }
}
export function interaction<TState, TRequest, TResponse>(definition: {
  id: string;
  requestSchema: z.ZodType<TRequest>;
  responseSchema: z.ZodType<TResponse>;
  request: (state: TState) => TRequest;
  handle: (request: TRequest) => TResponse | Promise<TResponse>;
  apply: (state: TState, response: TResponse) => TState;
  persist?: boolean;
}): Interaction<TState, TRequest, TResponse> {
  return new InteractionImplementation(
    definition.id,
    definition.persist,
    definition.requestSchema,
    definition.responseSchema,
    definition.request,
    definition.handle,
    definition.apply,
  );
}

interface CapabilityCompileContext<TState> {
  readonly id: string;
  readonly index: number;
  readonly stateSchema: z.ZodType<TState>;
  readonly callbacks: Record<string, Callback>;
}
export interface Capability<TState> {
  readonly name: string;
  readonly [compileCapabilityBrand]: (context: CapabilityCompileContext<TState>) => object;
}
class CapabilityImplementation<TState, TRequest> implements Capability<TState> {
  readonly requestJsonSchema: string;
  constructor(
    readonly name: string,
    readonly schema: z.ZodType<TRequest>,
    readonly apply: (state: TState, request: TRequest) => TState,
    readonly summarize: (request: TRequest) => string,
  ) {
    this.requestJsonSchema = jsonSchema(schema, `capability '${name}' schema`);
  }
  [compileCapabilityBrand]({
    id,
    index,
    stateSchema,
    callbacks,
  }: CapabilityCompileContext<TState>): object {
    const prefix = `${id}.capability.${index}`,
      validate = `${prefix}.validate`,
      apply = `${prefix}.apply`,
      summary = `${prefix}.summary`;
    callbacks[validate] = (_, input) => issues(this.schema, input);
    callbacks[apply] = (state, input) =>
      JSON.stringify(
        parse(
          stateSchema,
          this.apply(
            parseJson(stateSchema, state, `${id} state`),
            parseJson(this.schema, input, `${id} capability '${this.name}' request`),
          ),
          `${id} applied state`,
        ),
      );
    callbacks[summary] = (_, input) =>
      this.summarize(parseJson(this.schema, input, `${id} capability '${this.name}' request`));
    return {
      name: this.name,
      jsonSchema: this.requestJsonSchema,
      validateCallback: validate,
      applyCallback: apply,
      summaryCallback: summary,
      contractName: `${id}.capability.${this.name}`,
    };
  }
}
export function capability<TState, TRequest>(definition: {
  readonly name: string;
  readonly schema: z.ZodType<TRequest>;
  readonly apply: (state: TState, request: TRequest) => TState;
  readonly summarize: (request: TRequest) => string;
}): Capability<TState> {
  return new CapabilityImplementation(
    definition.name,
    definition.schema,
    definition.apply,
    definition.summarize,
  );
}

export interface OpenAiCompatibleChatClient {
  readonly kind: "openai-compatible";
  readonly version: 1;
  readonly endpoint: string;
  readonly model: string;
  readonly wireApi: "completions" | "responses";
  readonly apiKeyEnvironmentVariable?: string;
  readonly reasoningEffort?: "low" | "medium" | "high";
  readonly verifyModel?: boolean;
}
export type ChatClient = OpenAiCompatibleChatClient;
export interface AgentDefinition<TState, TOutput = never> {
  readonly id: string;
  readonly instructions: string;
  readonly client: ChatClient;
  readonly message: (state: TState) => string;
  readonly output?: {
    readonly schema: z.ZodType<TOutput>;
    readonly apply: (state: TState, output: TOutput) => TState;
  };
  readonly capabilities?: readonly Capability<TState>[];
  readonly continueSession?: boolean;
  readonly timeoutMs?: number;
  readonly persist?: boolean;
}
class AgentImplementation<TState, TOutput>
  extends NodeImplementation<TState>
  implements Agent<TState>
{
  readonly kind = "agent";
  constructor(
    id: string,
    persist: boolean | undefined,
    readonly instructions: string,
    readonly client: ChatClient,
    readonly message: (state: TState) => string,
    readonly output:
      | { schema: z.ZodType<TOutput>; apply: (state: TState, output: TOutput) => TState }
      | undefined,
    readonly granted: readonly Capability<TState>[],
    readonly continueSession: boolean,
    readonly timeoutMs?: number,
  ) {
    super(id, persist);
  }
}
export function agent<TState, TOutput = never>(
  definition: AgentDefinition<TState, TOutput>,
): Agent<TState> {
  const capabilities = definition.capabilities ?? [];
  const names = new Set<string>();
  for (const item of capabilities) {
    if (names.has(item.name)) {
      throw new TandemError(`Agent '${definition.id}' has duplicate capability '${item.name}'.`);
    } else {
      names.add(item.name);
    }
  }
  return new AgentImplementation(
    definition.id,
    definition.persist,
    definition.instructions,
    definition.client,
    definition.message,
    definition.output,
    capabilities,
    definition.continueSession ?? false,
    definition.timeoutMs,
  );
}

class TerminalImplementation<TState>
  extends NodeImplementation<TState>
  implements Terminal<TState>
{
  readonly kind = "terminal";
  constructor(
    id: string,
    persist: boolean | undefined,
    readonly failed: boolean,
    readonly summary: (state: TState) => string,
  ) {
    super(id, persist);
  }
}
export function output<TState>(definition: {
  id: string;
  summary: (state: TState) => string;
  failed?: boolean;
  persist?: boolean;
}): Terminal<TState> {
  return new TerminalImplementation(
    definition.id,
    definition.persist,
    definition.failed ?? false,
    definition.summary,
  );
}

export interface OrdinaryRoute<TState> {
  readonly from: Stage<TState> | Interaction<TState, unknown, unknown>;
  readonly to: Node<TState>;
  readonly label: string;
  readonly outcome?: never;
  readonly when?: (state: TState) => boolean;
}
export interface AgentOutcomeRoute<TState> {
  readonly from: Agent<TState>;
  readonly to: Node<TState>;
  readonly label: string;
  readonly outcome: "success" | "failed";
  readonly when?: (state: TState) => boolean;
}
export type Route<TState> = OrdinaryRoute<TState> | AgentOutcomeRoute<TState>;
export function route<TState>(definition: OrdinaryRoute<TState>): OrdinaryRoute<TState>;
export function route<TState>(definition: AgentOutcomeRoute<TState>): AgentOutcomeRoute<TState>;
export function route<TState>(definition: Route<TState>): Route<TState> {
  return definition;
}

export interface Pipeline<TState> {
  readonly name: string;
  readonly state: z.ZodType<TState>;
  readonly nodes: readonly Node<TState>[];
  readonly start: Node<TState>;
  readonly routes: readonly Route<TState>[];
  readonly outputs: readonly Terminal<TState>[];
  readonly persist: boolean;
}
export function pipeline<TState>(definition: {
  name: string;
  state: z.ZodType<TState>;
  nodes: readonly Node<NoInfer<TState>>[];
  start: Node<NoInfer<TState>>;
  routes: readonly Route<NoInfer<TState>>[];
  outputs: readonly Terminal<NoInfer<TState>>[];
  persist?: boolean;
}): Pipeline<TState> {
  const members = new Set<Node<TState>>(definition.nodes);
  if (members.size !== definition.nodes.length) {
    throw new Error("Pipeline nodes must contain each participant object exactly once.");
  }
  const ids = new Set(definition.nodes.map((node) => node.id));
  if (ids.size !== definition.nodes.length) {
    throw new Error("Pipeline node IDs must be unique.");
  }
  if (!members.has(definition.start)) {
    throw new Error(
      `Pipeline start '${definition.start.id}' must be the registered participant object.`,
    );
  }
  for (const item of definition.routes) {
    if (!members.has(item.from) || !members.has(item.to)) {
      throw new Error(`Route '${item.label}' endpoints must be registered participant objects.`);
    }
  }
  for (const item of definition.outputs) {
    if (!members.has(item)) {
      throw new Error(`Output '${item.id}' must be the registered participant object.`);
    }
  }
  return { ...definition, persist: definition.persist ?? false };
}

export interface RunResult<TState> {
  readonly runId: string;
  readonly succeeded: boolean;
  readonly state: TState;
  readonly summary: string | null;
}
export interface RunOptions {
  readonly ledgerPath?: string;
  readonly signal?: AbortSignal;
}
const acceptedKinds = [
  "StructuredOutputAccepted",
  "CapabilityAccepted",
  "InteractionRequested",
  "InteractionAnswered",
  "StepCompleted",
] as const;
type AcceptedKind = (typeof acceptedKinds)[number];
export type AcceptedValue = {
  [K in AcceptedKind]: {
    readonly version: 1;
    readonly kind: K;
    readonly stepId: string;
    readonly valueType: string | null;
    readonly payload: unknown | null;
  };
}[AcceptedKind];
const acceptedValueSchema = z
  .object({
    kind: z.enum(acceptedKinds),
    StepId: z.string().min(1),
    ValueType: z.string().min(1).nullable(),
    Payload: z.unknown().nullable(),
  })
  .strict()
  .refine((value) => value.ValueType !== null || value.Payload !== null, {
    message: "ValueType and Payload cannot both be null",
  });
const acceptedValuesSchema = z.array(acceptedValueSchema);
const runResultSchema = z
  .object({
    runId: z.uuid(),
    succeeded: z.boolean(),
    state: z.unknown(),
    summary: z.string().nullable(),
  })
  .strict();

export async function inspectAccepted(options: {
  ledgerPath: string;
  runId: string;
}): Promise<readonly AcceptedValue[]> {
  try {
    return parseJson(
      acceptedValuesSchema,
      await inspectAcceptedAsync(options.ledgerPath, options.runId),
      "accepted values",
    ).map(
      (value) =>
        ({
          version: 1 as const,
          kind: value.kind,
          stepId: value.StepId,
          valueType: value.ValueType,
          payload: value.Payload,
        }) as AcceptedValue,
    );
  } catch (error) {
    if (error instanceof ContractValidationError) {
      throw error;
    }
    throw new TandemRuntimeError("inspect", error);
  }
}

export async function run<TState>(
  graph: Pipeline<TState>,
  initial: unknown,
  options: RunOptions = {},
): Promise<RunResult<TState>> {
  const initialState = parse(graph.state, initial, "initial state");
  const callbacks: Record<string, Callback> = {};
  const nodes = graph.nodes.map((node) => compileNode(node, graph.state, callbacks));
  const routes = graph.routes.map((item, index) => {
    const callback = item.when ? `route.${index}.when` : undefined;
    if (item.when) {
      callbacks[callback!] = (state) =>
        String(item.when!(parseJson(graph.state, state, `route '${item.label}' state`)));
    }
    return {
      source: item.from.id,
      target: item.to.id,
      label: item.label,
      outcome: item.outcome,
      predicateCallback: callback,
    };
  });
  try {
    const resultJson = await runRegisteredGraphAsync(
      JSON.stringify({
        contractVersion: 2,
        name: graph.name,
        start: graph.start.id,
        initialState: JSON.stringify(initialState),
        persist: graph.persist,
        ledgerPath: options.ledgerPath,
        nodes,
        routes,
        outputs: graph.outputs.map((item) => item.id),
        callbacks: Object.keys(callbacks),
      }),
      async (id: string, state: string, input: string) => {
        const callback = callbacks[id];
        if (!callback) {
          throw new Error(`Unknown internal callback '${id}'.`);
        }
        return await callback(state, input);
      },
      options.signal,
    );
    const result = parseJson(runResultSchema, resultJson, "run result");
    return { ...result, state: parse(graph.state, result.state, "final state") };
  } catch (error) {
    if (error instanceof ContractValidationError) {
      throw error;
    }
    if (options.signal?.aborted || (error instanceof Error && error.name === "AbortError")) {
      throw new TandemCancellationError(error);
    }
    throw new TandemRuntimeError("run", error);
  }
}

function issues<T>(schema: z.ZodType<T>, input: string): string {
  let value: unknown;
  try {
    value = JSON.parse(input);
  } catch {
    return JSON.stringify([{ path: "$", message: "Invalid JSON" }]);
  }
  try {
    parse(schema, value, "agent contract");
    return "";
  } catch (error) {
    return error instanceof ContractValidationError
      ? JSON.stringify(error.problems)
      : JSON.stringify([
          { path: "$", message: error instanceof Error ? error.message : String(error) },
        ]);
  }
}
function compileNode<TState>(
  node: Node<TState>,
  stateSchema: z.ZodType<TState>,
  callbacks: Record<string, Callback>,
): object {
  const implementation = node as NodeImplementation<TState>;
  const base = { id: node.id, persist: implementation.persist };
  if (implementation instanceof StageImplementation) {
    const run = `${node.id}.run`;
    callbacks[run] = async (state) =>
      JSON.stringify(
        parse(
          stateSchema,
          await implementation.execute(parseJson(stateSchema, state, `${node.id} input`)),
          `${node.id} output`,
        ),
      );
    return { ...base, kind: "stage", runCallback: run };
  }
  if (implementation instanceof InteractionImplementation) {
    const request = `${node.id}.request`,
      handle = `${node.id}.handle`,
      apply = `${node.id}.apply`;
    callbacks[request] = (state) =>
      JSON.stringify(
        parse(
          implementation.requestSchema,
          implementation.request(parseJson(stateSchema, state, `${node.id} state`)),
          `${node.id} request`,
        ),
      );
    callbacks[handle] = async (_, input) =>
      JSON.stringify(
        parse(
          implementation.responseSchema,
          await implementation.handle(
            parseJson(implementation.requestSchema, input, `${node.id} request input`),
          ),
          `${node.id} response`,
        ),
      );
    callbacks[apply] = (state, input) =>
      JSON.stringify(
        parse(
          stateSchema,
          implementation.apply(
            parseJson(stateSchema, state, `${node.id} state`),
            parseJson(implementation.responseSchema, input, `${node.id} response input`),
          ),
          `${node.id} applied state`,
        ),
      );
    return {
      ...base,
      kind: "interaction",
      requestCallback: request,
      handleCallback: handle,
      applyCallback: apply,
    };
  }
  if (implementation instanceof AgentImplementation) {
    const message = `${node.id}.message`;
    callbacks[message] = (state) =>
      implementation.message(parseJson(stateSchema, state, `${node.id} message state`));
    const output = implementation.output
      ? compileAgentOutput(node.id, implementation.output, stateSchema, callbacks)
      : undefined;
    const capabilities = implementation.granted.map((item, index) =>
      item[compileCapabilityBrand]({ id: node.id, index, stateSchema, callbacks }),
    );
    return {
      ...base,
      kind: "agent",
      instructions: implementation.instructions,
      client: { ...implementation.client, verifyModel: implementation.client.verifyModel ?? false },
      messageCallback: message,
      output,
      capabilities,
      continueSession: implementation.continueSession,
      timeoutMilliseconds: implementation.timeoutMs,
    };
  }
  const terminal = implementation as TerminalImplementation<TState>,
    summary = `${node.id}.summary`;
  callbacks[summary] = (state) =>
    terminal.summary(parseJson(stateSchema, state, `${node.id} state`));
  return { ...base, kind: terminal.failed ? "failure" : "completion", summaryCallback: summary };
}

function compileAgentOutput<TState, TOutput>(
  id: string,
  output: { schema: z.ZodType<TOutput>; apply: (state: TState, output: TOutput) => TState },
  stateSchema: z.ZodType<TState>,
  callbacks: Record<string, Callback>,
): object {
  const validate = `${id}.output.validate`,
    apply = `${id}.output.apply`;
  callbacks[validate] = (_, input) => issues(output.schema, input);
  callbacks[apply] = (state, input) =>
    JSON.stringify(
      parse(
        stateSchema,
        output.apply(
          parseJson(stateSchema, state, `${id} state`),
          parseJson(output.schema, input, `${id} output`),
        ),
        `${id} applied state`,
      ),
    );
  return {
    jsonSchema: jsonSchema(output.schema, `${id} output schema`),
    validateCallback: validate,
    applyCallback: apply,
    contractName: `${id}.output`,
  };
}
