import { isDeepStrictEqual } from "node:util";
import { z } from "zod";

type SyncCallback = (state: string, input: string) => string;
type AsyncCallback = (state: string, input: string, signal: AbortSignal) => Promise<string>;
const participantBrand: unique symbol = Symbol("participant");
const compileCapabilityBrand: unique symbol = Symbol("compileCapability");
const interactionHandlersBrand: unique symbol = Symbol("interactionHandlers");
const workspaceBrand: unique symbol = Symbol("workspace");
const toolGroupBrand: unique symbol = Symbol("toolGroup");
const commandSelectionBrand: unique symbol = Symbol("commandSelection");

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
  readonly problems: readonly ValidationProblem[];
  constructor(
    readonly boundary: string,
    problems: readonly ValidationProblem[],
  ) {
    super(
      `${boundary} validation failed: ${problems.map((p) => `${p.path}: ${p.message}`).join("; ")}`,
    );
    this.name = "ContractValidationError";
    this.problems = problems;
  }
}

export type ValidationProblem = {
  readonly path: string;
  readonly message: string;
};

type CallbackFailure = {
  readonly boundary: string;
  readonly problems: readonly ValidationProblem[];
};
type CallbackResult =
  | { readonly succeeded: true; readonly value: string }
  | {
      readonly succeeded: false;
      readonly error: {
        readonly name: string;
        readonly message: string;
        readonly boundary?: string;
        readonly problems?: readonly ValidationProblem[];
      };
    };

function callbackError(error: unknown): Extract<CallbackResult, { succeeded: false }>["error"] {
  return error instanceof ContractValidationError
    ? {
        name: error.name,
        message: error.message,
        boundary: error.boundary,
        problems: error.problems,
      }
    : {
        name: error instanceof Error ? error.name : "Error",
        message: error instanceof Error ? error.message : String(error),
      };
}

class CallbackRegistry {
  readonly #sync = new Map<string, SyncCallback>();
  readonly #async = new Map<string, AsyncCallback>();
  #next = 0;
  #disposed = false;

  registerSync(callback: SyncCallback): string {
    const id = this.#allocate();
    this.#sync.set(id, callback);
    return id;
  }

  registerAsync(callback: AsyncCallback): string {
    const id = this.#allocate();
    this.#async.set(id, callback);
    return id;
  }

  invokeSync(id: string, state: string, input: string): string {
    try {
      const callback = this.#sync.get(id);
      if (!callback) {
        throw new Error(`Unknown internal callback '${id}'.`);
      }
      return JSON.stringify({
        succeeded: true,
        value: callback(state, input),
      } satisfies CallbackResult);
    } catch (error) {
      return JSON.stringify({
        succeeded: false,
        error: callbackError(error),
      } satisfies CallbackResult);
    }
  }

  async invokeAsync(
    id: string,
    state: string,
    input: string,
    signal: AbortSignal,
  ): Promise<string> {
    try {
      const callback = this.#async.get(id);
      if (!callback) {
        throw new Error(`Unknown internal async callback '${id}'.`);
      }
      return JSON.stringify({
        succeeded: true,
        value: await callback(state, input, signal),
      } satisfies CallbackResult);
    } catch (error) {
      return JSON.stringify({
        succeeded: false,
        error: callbackError(error),
      } satisfies CallbackResult);
    }
  }

  dispose(): void {
    this.#disposed = true;
    this.#sync.clear();
    this.#async.clear();
  }

  #allocate(): string {
    if (this.#disposed) {
      throw new Error("Callback registry has been disposed.");
    }
    return `c${this.#next++}`;
  }
}

function callbackContractFailure(error: unknown): CallbackFailure | null {
  const marker = "TANDEM_CALLBACK_CONTRACT:";
  const message = error instanceof Error ? error.message : String(error);
  const start = message.indexOf(marker);
  if (start < 0) {
    return null;
  }
  try {
    return JSON.parse(message.slice(start + marker.length)) as CallbackFailure;
  } catch {
    return null;
  }
}

function isCancellationError(error: unknown, signalAborted: boolean): boolean {
  if (!(error instanceof Error)) {
    return false;
  }
  if (error.message.includes("JavaScript callback failed:") && !signalAborted) {
    return false;
  }
  return (
    error.name === "AbortError" ||
    /\b(?:operation was cancel(?:l)?ed|operation was aborted|this operation was aborted)\b/i.test(
      error.message,
    )
  );
}

function path(parts: PropertyKey[]): string {
  return parts.length === 0
    ? "$"
    : `$${parts.map((part) => (typeof part === "number" ? `[${part}]` : `.${String(part)}`)).join("")}`;
}
function parseValidated<T>(schema: z.ZodType<T>, value: unknown, boundary: string): T {
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
  return result.data;
}
function parse<T>(schema: z.ZodType<T>, value: unknown, boundary: string): T {
  const result = parseValidated(schema, value, boundary);
  if (!isDeepStrictEqual(result, value)) {
    throw new ContractValidationError(boundary, [
      {
        path: "$",
        message:
          "Zod contract changed the boundary value. Coercion, defaults, transforms, and stripping are unsupported.",
      },
    ]);
  }
  return result;
}
function serializeBoundary<T>(schema: z.ZodType<T>, value: unknown, boundary: string): string {
  const parsed = parse(schema, value, boundary);
  const problem = jsonValueProblem(parsed, "$", new WeakSet<object>());
  if (problem) {
    throw new ContractValidationError(boundary, [problem]);
  }
  let json: string | undefined;
  try {
    json = JSON.stringify(parsed);
  } catch (error) {
    throw new ContractValidationError(boundary, [
      {
        path: "$",
        message: `Value is not JSON-serializable: ${error instanceof Error ? error.message : String(error)}`,
      },
    ]);
  }
  if (json === undefined) {
    throw new ContractValidationError(boundary, [
      { path: "$", message: "Top-level undefined is not JSON-serializable." },
    ]);
  }
  const roundTripped = JSON.parse(json) as unknown;
  if (!isDeepStrictEqual(roundTripped, parsed)) {
    throw new ContractValidationError(boundary, [
      { path: "$", message: "Value is not losslessly JSON-serializable." },
    ]);
  }
  return json;
}

function jsonValueProblem(
  value: unknown,
  valuePath: string,
  seen: WeakSet<object>,
): ValidationProblem | null {
  if (value === undefined) {
    return { path: valuePath, message: "undefined is not JSON-serializable." };
  }
  if (typeof value === "number" && !Number.isFinite(value)) {
    return { path: valuePath, message: "Non-finite numbers are not JSON-serializable." };
  }
  if (typeof value === "bigint" || typeof value === "symbol" || typeof value === "function") {
    return { path: valuePath, message: `${typeof value} values are not JSON-serializable.` };
  }
  if (value === null || typeof value !== "object") {
    return null;
  }
  if (seen.has(value)) {
    return { path: valuePath, message: "Cyclic values are not JSON-serializable." };
  }
  seen.add(value);
  if (Object.getOwnPropertySymbols(value).length > 0) {
    return { path: valuePath, message: "Symbol properties are not JSON-serializable." };
  }
  if (Array.isArray(value)) {
    for (let index = 0; index < value.length; index++) {
      const descriptor = Object.getOwnPropertyDescriptor(value, index);
      if (!descriptor) {
        return {
          path: `${valuePath}[${index}]`,
          message: "Sparse arrays are not JSON-serializable.",
        };
      }
      if (!("value" in descriptor)) {
        return {
          path: `${valuePath}[${index}]`,
          message: "Accessor properties are not supported at JSON boundaries.",
        };
      }
      const problem = jsonValueProblem(descriptor.value, `${valuePath}[${index}]`, seen);
      if (problem) {
        return problem;
      }
    }
    const additionalProperty = Object.getOwnPropertyNames(value).find((name) => {
      if (name === "length") {
        return false;
      }
      const index = Number(name);
      return (
        !Number.isInteger(index) || index < 0 || index >= value.length || String(index) !== name
      );
    });
    if (additionalProperty) {
      return {
        path: `${valuePath}.${additionalProperty}`,
        message: "Additional array properties are not JSON-serializable.",
      };
    }
    seen.delete(value);
    return null;
  }
  if (Object.getPrototypeOf(value) !== Object.prototype && Object.getPrototypeOf(value) !== null) {
    return {
      path: valuePath,
      message: "Only plain objects are JSON-serializable boundary values.",
    };
  }
  for (const name of Object.getOwnPropertyNames(value)) {
    const descriptor = Object.getOwnPropertyDescriptor(value, name);
    if (!descriptor?.enumerable) {
      return {
        path: `${valuePath}.${name}`,
        message: "Non-enumerable properties are not JSON-serializable.",
      };
    }
    if (!("value" in descriptor)) {
      return {
        path: `${valuePath}.${name}`,
        message: "Accessor properties are not supported at JSON boundaries.",
      };
    }
    const problem = jsonValueProblem(descriptor.value, `${valuePath}.${name}`, seen);
    if (problem) {
      return problem;
    }
  }
  seen.delete(value);
  return null;
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
function inputJsonSchema<T>(schema: z.ZodType<T>, boundary: string): string {
  try {
    z.toJSONSchema(schema, { io: "output" });
    return JSON.stringify(z.toJSONSchema(schema, { io: "input" }));
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
export interface Parallel<TState> extends Participant<TState> {
  readonly kind: "parallel";
}
export interface Terminal<TState> extends Participant<TState> {
  readonly kind: "terminal";
}
type Node<TState> =
  | Stage<TState>
  | Interaction<TState, unknown, unknown>
  | Agent<TState>
  | Parallel<TState>
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
    readonly execute: (
      state: TState,
      context: { readonly signal: AbortSignal },
    ) => TState | Promise<TState>,
  ) {
    super(id, persist);
  }
}
export function stage<TState>(definition: {
  id: string;
  execute: (state: TState, context: { readonly signal: AbortSignal }) => TState | Promise<TState>;
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
  apply: (state: TState, response: TResponse) => TState;
  persist?: boolean;
}): Interaction<TState, TRequest, TResponse> {
  return new InteractionImplementation(
    definition.id,
    definition.persist,
    definition.requestSchema,
    definition.responseSchema,
    definition.request,
    definition.apply,
  );
}

type InteractionHandler<TRequest, TResponse> = (
  request: TRequest,
  context: { readonly signal: AbortSignal },
) => TResponse | Promise<TResponse>;
type RegisteredInteractionHandler = {
  readonly interaction: Interaction<unknown, unknown, unknown>;
  readonly handle: InteractionHandler<unknown, unknown>;
};
export interface InteractionHandlers {
  handle<TState, TRequest, TResponse>(
    interaction: Interaction<TState, TRequest, TResponse>,
    handler: InteractionHandler<TRequest, TResponse>,
  ): InteractionHandlers;
  readonly [interactionHandlersBrand]: true;
}
class InteractionHandlersImplementation implements InteractionHandlers {
  readonly [interactionHandlersBrand] = true;
  readonly entries: RegisteredInteractionHandler[] = [];
  readonly #interactions = new Set<Interaction<unknown, unknown, unknown>>();

  handle<TState, TRequest, TResponse>(
    interaction: Interaction<TState, TRequest, TResponse>,
    handler: InteractionHandler<TRequest, TResponse>,
  ): InteractionHandlers {
    const opaque = interaction as Interaction<unknown, unknown, unknown>;
    if (this.#interactions.has(opaque)) {
      throw new TandemError(`Interaction '${interaction.id}' already has a handler.`);
    }
    this.#interactions.add(opaque);
    this.entries.push({
      interaction: opaque,
      handle: handler as InteractionHandler<unknown, unknown>,
    });
    return this;
  }
}
export function interactions(): InteractionHandlers {
  return new InteractionHandlersImplementation();
}

interface CapabilityCompileContext<TState> {
  readonly id: string;
  readonly stateSchema: z.ZodType<TState>;
  readonly callbacks: CallbackRegistry;
}
export interface Capability<TState> {
  readonly name: string;
  readonly [compileCapabilityBrand]: (context: CapabilityCompileContext<TState>) => object;
}
class CapabilityImplementation<TState, TRequest> implements Capability<TState> {
  readonly requestJsonSchema: string;
  constructor(
    readonly name: string,
    readonly instructions: string,
    readonly schema: z.ZodType<TRequest>,
    readonly validateFor:
      | ((state: TState, request: TRequest) => readonly ValidationProblem[])
      | undefined,
    readonly apply: (state: TState, request: TRequest) => TState,
    readonly summarize: (request: TRequest) => string,
  ) {
    this.requestJsonSchema = inputJsonSchema(schema, `capability '${name}' schema`);
  }
  [compileCapabilityBrand]({
    id,
    stateSchema,
    callbacks,
  }: CapabilityCompileContext<TState>): object {
    const validate = callbacks.registerSync((_, input) => issues(this.schema, input));
    const validateFor = this.validateFor
      ? callbacks.registerSync((state, input) =>
          validationProblems(
            this.validateFor!(
              parseJson(stateSchema, state, `${id} state`),
              parseJson(this.schema, input, `${id} capability '${this.name}' request`),
            ),
            `${id} capability '${this.name}' contextual validation`,
          ),
        )
      : undefined;
    const apply = callbacks.registerSync((state, input) =>
      serializeBoundary(
        stateSchema,
        this.apply(
          parseJson(stateSchema, state, `${id} state`),
          parseJson(this.schema, input, `${id} capability '${this.name}' request`),
        ),
        `${id} applied state`,
      ),
    );
    const summary = callbacks.registerSync((_, input) =>
      this.summarize(parseJson(this.schema, input, `${id} capability '${this.name}' request`)),
    );
    return {
      name: this.name,
      instructions: this.instructions,
      jsonSchema: this.requestJsonSchema,
      validateCallback: validate,
      validateForCallback: validateFor,
      applyCallback: apply,
      summaryCallback: summary,
      valueType: `${id}.capability.${this.name}`,
    };
  }
}
export function capability<TState, TRequest>(definition: {
  readonly name: string;
  readonly instructions: string;
  readonly schema: z.ZodType<TRequest>;
  readonly validateFor?: (state: TState, request: TRequest) => readonly ValidationProblem[];
  readonly apply: (state: TState, request: TRequest) => TState;
  readonly summarize: (request: TRequest) => string;
}): Capability<TState> {
  requireInstructions(definition.instructions, `Capability '${definition.name}' instructions`);
  return new CapabilityImplementation(
    definition.name,
    definition.instructions,
    definition.schema,
    definition.validateFor,
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
  readonly reasoningEffort?: "none" | "low" | "medium" | "high";
  readonly verifyModel?: boolean;
}
export type ChatClient = OpenAiCompatibleChatClient;
export interface AgentSkill {
  readonly directory: string;
}

export type AgentToolName =
  | "read_file"
  | "ls"
  | "grep"
  | "write_file"
  | "delete_file"
  | "replace"
  | "replace_lines"
  | "git:ro"
  | "shell"
  | "web_search"
  | "web_fetch";
interface AgentCommandArgumentBase {
  readonly name: string;
  readonly description: string;
  readonly flag: string;
  readonly maxLength?: number;
}
export type AgentCommandArgument = AgentCommandArgumentBase &
  (
    | { readonly pattern: string; readonly allowedValues?: never }
    | { readonly allowedValues: readonly string[]; readonly pattern?: never }
  );
export interface AgentCommand {
  readonly name: string;
  readonly description: string;
  readonly command: string;
  readonly arguments?: readonly AgentCommandArgument[];
}
interface AgentCommandSelection {
  readonly [commandSelectionBrand]: object;
}
type AgentToolSelection = AgentToolName | AgentCommandSelection;
export interface AgentToolGroup<TState> {
  readonly [toolGroupBrand]: (state: TState) => boolean;
}
export type AgentToolEffect =
  | "read"
  | "workspaceMutation"
  | "processExecution"
  | "lifecycleTransition"
  | "unclassified";
export interface AgentToolInvocation {
  readonly name: string;
  readonly effect: AgentToolEffect;
  readonly arguments: unknown;
}
export type AgentToolInterceptor<TState> = (
  state: TState,
  invocation: AgentToolInvocation,
  context: { readonly signal: AbortSignal },
) => string | null | Promise<string | null>;
class AgentToolGroupImplementation<TState> implements AgentToolGroup<TState> {
  readonly [toolGroupBrand]: (state: TState) => boolean;
  constructor(
    readonly predicate: ((state: TState) => boolean) | undefined,
    readonly tools: readonly AgentToolSelection[],
  ) {
    this[toolGroupBrand] = predicate ?? (() => true);
  }
}
function createToolGroup<TState>(
  predicate: ((state: TState) => boolean) | undefined,
  tools: readonly AgentToolSelection[],
): AgentToolGroup<TState> {
  if (tools.length === 0) {
    throw new TandemError("An agent tool group cannot be empty.");
  }
  const seen = new Set<AgentToolSelection>();
  for (const tool of tools) {
    if (typeof tool !== "string" && !(commandSelectionBrand in tool)) {
      throw new TandemError("Agent tools must be built-in names or workspace.commands.");
    }
    if (seen.has(tool)) {
      throw new TandemError("An agent tool group cannot select a tool twice.");
    }
    seen.add(tool);
  }
  return new AgentToolGroupImplementation(predicate, tools);
}
export const agentTools = {
  always: (...tools: readonly AgentToolSelection[]): AgentToolGroup<never> =>
    createToolGroup(undefined, tools),
  when: <TState>(
    predicate: (state: TState) => boolean,
    ...tools: readonly AgentToolSelection[]
  ): AgentToolGroup<TState> => createToolGroup(predicate, tools),
};
export interface AgentWorkspaceConfiguration<TState> {
  readonly [workspaceBrand]: (state: TState) => TState;
}
export interface AgentWorkspace<TState> {
  readonly commands: AgentCommandSelection;
  withTools(
    groups: readonly (AgentToolGroup<TState> | AgentToolGroup<never>)[],
    options?: { readonly interceptTool?: AgentToolInterceptor<TState> },
  ): AgentWorkspaceConfiguration<TState>;
}
class AgentWorkspaceImplementation<TState> implements AgentWorkspace<TState> {
  readonly commands: AgentCommandSelection;
  readonly commandSource:
    | readonly AgentCommand[]
    | ((state: TState) => readonly AgentCommand[])
    | undefined;
  constructor(
    readonly path: (state: TState) => string,
    commandSource:
      | readonly AgentCommand[]
      | ((state: TState) => readonly AgentCommand[])
      | undefined,
  ) {
    this.commandSource =
      typeof commandSource === "function" || commandSource === undefined
        ? commandSource
        : commandSource.map(copyAgentCommand);
    this.commands = { [commandSelectionBrand]: this };
  }
  withTools(
    groups: readonly (AgentToolGroup<TState> | AgentToolGroup<never>)[],
    options?: { readonly interceptTool?: AgentToolInterceptor<TState> },
  ): AgentWorkspaceConfiguration<TState> {
    if (groups.length === 0) {
      throw new TandemError("An agent workspace requires tool groups.");
    }
    const implementations = groups.map((group) => {
      if (!(group instanceof AgentToolGroupImplementation)) {
        throw new TandemError("Agent tool groups must be created by agentTools.");
      }
      return group as AgentToolGroupImplementation<TState>;
    });
    if (options?.interceptTool !== undefined && typeof options.interceptTool !== "function") {
      throw new TandemError("Workspace tool interceptor must be a function.");
    }
    return new AgentWorkspaceConfigurationImplementation(
      this,
      implementations,
      options?.interceptTool,
    );
  }
}
class AgentWorkspaceConfigurationImplementation<
  TState,
> implements AgentWorkspaceConfiguration<TState> {
  readonly [workspaceBrand]!: (state: TState) => TState;
  constructor(
    readonly workspace: AgentWorkspaceImplementation<TState>,
    readonly groups: readonly AgentToolGroupImplementation<TState>[],
    readonly interceptTool: AgentToolInterceptor<TState> | undefined,
  ) {}
}
export function agentWorkspace<TState>(definition: {
  readonly path: (state: TState) => string;
  readonly commands?: readonly AgentCommand[] | ((state: TState) => readonly AgentCommand[]);
}): AgentWorkspace<TState> {
  if (typeof definition.path !== "function") {
    throw new TandemError("Workspace path is required.");
  }
  if (definition.commands !== undefined && typeof definition.commands !== "function") {
    validateAgentCommands(definition.commands, "Workspace commands");
  }
  return new AgentWorkspaceImplementation(definition.path, definition.commands);
}

function copyAgentCommand(command: AgentCommand): AgentCommand {
  return command.arguments === undefined
    ? { ...command }
    : {
        ...command,
        arguments: command.arguments.map((argument) =>
          argument.pattern !== undefined
            ? { ...argument }
            : { ...argument, allowedValues: [...argument.allowedValues] },
        ),
      };
}

function validateAgentCommands(
  commands: unknown,
  context: string,
): asserts commands is readonly AgentCommand[] {
  if (!Array.isArray(commands)) {
    throw new TandemError(`${context} must be an array.`);
  }
  for (const [commandIndex, command] of commands.entries()) {
    const commandContext = `${context}[${commandIndex}]`;
    if (typeof command !== "object" || command === null) {
      throw new TandemError(`${commandContext} must be a command.`);
    }
    const candidate = command as Partial<AgentCommand>;
    if (typeof candidate.name !== "string" || !/^[A-Za-z_][A-Za-z0-9_]*$/.test(candidate.name)) {
      throw new TandemError(`${commandContext}.name must be a valid tool name.`);
    }
    if (typeof candidate.description !== "string" || candidate.description.trim().length === 0) {
      throw new TandemError(`${commandContext}.description must be non-blank.`);
    }
    if (typeof candidate.command !== "string" || candidate.command.trim().length === 0) {
      throw new TandemError(`${commandContext}.command must be non-blank.`);
    }
    if (candidate.arguments !== undefined && !Array.isArray(candidate.arguments)) {
      throw new TandemError(`${commandContext}.arguments must be an array.`);
    }
    const argumentNames = new Set<string>();
    for (const [argumentIndex, argument] of (candidate.arguments ?? []).entries()) {
      const argumentContext = `${commandContext}.arguments[${argumentIndex}]`;
      if (typeof argument !== "object" || argument === null) {
        throw new TandemError(`${argumentContext} must be an argument.`);
      }
      const candidateArgument = argument as Partial<AgentCommandArgument>;
      if (
        typeof candidateArgument.name !== "string" ||
        !/^[A-Za-z_][A-Za-z0-9_]*$/.test(candidateArgument.name)
      ) {
        throw new TandemError(`${argumentContext}.name must be a valid JSON property identifier.`);
      }
      if (argumentNames.has(candidateArgument.name)) {
        throw new TandemError(`${argumentContext}.name duplicates '${candidateArgument.name}'.`);
      }
      argumentNames.add(candidateArgument.name);
      if (
        typeof candidateArgument.description !== "string" ||
        candidateArgument.description.trim().length === 0
      ) {
        throw new TandemError(`${argumentContext}.description must be non-blank.`);
      }
      if (
        typeof candidateArgument.flag !== "string" ||
        candidateArgument.flag.trim().length === 0 ||
        /\s/.test(candidateArgument.flag)
      ) {
        throw new TandemError(`${argumentContext}.flag must be a whitespace-free switch token.`);
      }
      if (
        candidateArgument.pattern !== undefined &&
        typeof candidateArgument.pattern !== "string"
      ) {
        throw new TandemError(`${argumentContext}.pattern must be a string.`);
      }
      if (
        candidateArgument.allowedValues !== undefined &&
        !Array.isArray(candidateArgument.allowedValues)
      ) {
        throw new TandemError(`${argumentContext}.allowedValues must be an array.`);
      }
      const hasPattern = typeof candidateArgument.pattern === "string";
      const hasAllowedValues = Array.isArray(candidateArgument.allowedValues);
      if (hasPattern === hasAllowedValues) {
        throw new TandemError(
          `${argumentContext} requires exactly one of pattern or allowedValues.`,
        );
      }
      if (
        candidateArgument.maxLength !== undefined &&
        (!Number.isSafeInteger(candidateArgument.maxLength) ||
          candidateArgument.maxLength <= 0 ||
          candidateArgument.maxLength > 2_147_483_647)
      ) {
        throw new TandemError(`${argumentContext}.maxLength must be a positive integer.`);
      }
      if (candidateArgument.allowedValues !== undefined) {
        if (candidateArgument.allowedValues.length === 0) {
          throw new TandemError(`${argumentContext}.allowedValues must not be empty.`);
        }
        const values = new Set<string>();
        for (const value of candidateArgument.allowedValues) {
          if (typeof value !== "string" || value.trim().length === 0) {
            throw new TandemError(`${argumentContext}.allowedValues must be non-blank strings.`);
          }
          if (values.has(value)) {
            throw new TandemError(`${argumentContext}.allowedValues duplicates '${value}'.`);
          }
          values.add(value);
        }
      }
    }
  }
}
export function skill(definition: { readonly directory: string }): AgentSkill {
  if (typeof definition.directory !== "string" || definition.directory.trim().length === 0) {
    throw new TandemError("Skill directory must be a non-blank string.");
  }
  return { directory: definition.directory };
}
export interface AgentDefinition<TState, TOutput = never> {
  readonly id: string;
  readonly instructions: string;
  readonly client: ChatClient;
  readonly message: (state: TState) => string;
  readonly output?: {
    readonly instructions: string;
    readonly schema: z.ZodType<TOutput>;
    readonly validateFor?: (state: TState, output: TOutput) => readonly ValidationProblem[];
    readonly apply: (state: TState, output: TOutput) => TState;
  };
  readonly capabilities?: readonly Capability<TState>[];
  readonly skills?: readonly AgentSkill[];
  readonly workspace?: AgentWorkspaceConfiguration<TState>;
  readonly temperature?: number;
  readonly maxOutputTokens?: number;
  readonly continueSession?: boolean;
  readonly checkpoint?: {
    readonly contextWindowTokens: number;
    readonly maxOutputTokens: number;
    readonly checkpointAtPercent: number;
    readonly capability: Capability<TState>;
    readonly instructions: string;
    readonly message: (state: TState, currentContextTokens: number) => string;
    readonly session?: "retain" | "reset";
    readonly disableCompaction?: boolean;
  };
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
      | {
          instructions: string;
          schema: z.ZodType<TOutput>;
          validateFor?: (state: TState, output: TOutput) => readonly ValidationProblem[];
          apply: (state: TState, output: TOutput) => TState;
        }
      | undefined,
    readonly granted: readonly Capability<TState>[],
    readonly skills: readonly AgentSkill[],
    readonly workspace: AgentWorkspaceConfiguration<TState> | undefined,
    readonly temperature: number | undefined,
    readonly maxOutputTokens: number | undefined,
    readonly continueSession: boolean,
    readonly checkpoint: AgentDefinition<TState>["checkpoint"],
    readonly timeoutMs?: number,
  ) {
    super(id, persist);
  }
}
export function agent<TState, TOutput = never>(
  definition: AgentDefinition<TState, TOutput>,
): Agent<TState> {
  requireInstructions(definition.instructions, `Agent '${definition.id}' instructions`);
  if (
    definition.client.reasoningEffort !== undefined &&
    !["none", "low", "medium", "high"].includes(definition.client.reasoningEffort)
  ) {
    throw new TandemError(`Agent '${definition.id}' has an invalid reasoning effort.`);
  }
  if (definition.output) {
    requireInstructions(
      definition.output.instructions,
      `Agent '${definition.id}' output instructions`,
    );
  }
  const capabilities = definition.capabilities ?? [];
  const names = new Set<string>();
  for (const item of capabilities) {
    if (names.has(item.name)) {
      throw new TandemError(`Agent '${definition.id}' has duplicate capability '${item.name}'.`);
    } else {
      names.add(item.name);
    }
  }
  const skills = definition.skills ?? [];
  const skillDirectories = new Set<string>();
  for (const item of skills) {
    if (typeof item?.directory !== "string" || item.directory.trim().length === 0) {
      throw new TandemError(`Agent '${definition.id}' has a skill with an invalid directory.`);
    }
    if (!skillDirectories.add(item.directory)) {
      throw new TandemError(
        `Agent '${definition.id}' has the skill directory '${item.directory}' more than once.`,
      );
    }
  }
  if (
    definition.temperature !== undefined &&
    (!Number.isFinite(definition.temperature) ||
      definition.temperature < 0 ||
      definition.temperature > 2)
  ) {
    throw new TandemError(`Agent '${definition.id}' temperature must be between 0 and 2.`);
  }
  if (
    definition.maxOutputTokens !== undefined &&
    (!Number.isSafeInteger(definition.maxOutputTokens) ||
      definition.maxOutputTokens <= 0 ||
      definition.maxOutputTokens > 2_147_483_647)
  ) {
    throw new TandemError(
      `Agent '${definition.id}' maxOutputTokens must be a positive 32-bit integer.`,
    );
  }
  if (definition.checkpoint) {
    const checkpoint = definition.checkpoint;
    if (
      !Number.isSafeInteger(checkpoint.contextWindowTokens) ||
      checkpoint.contextWindowTokens <= 0 ||
      checkpoint.contextWindowTokens > 2_147_483_647
    ) {
      throw new TandemError(
        `Agent '${definition.id}' checkpoint contextWindowTokens must be a positive 32-bit integer.`,
      );
    }
    if (
      !Number.isSafeInteger(checkpoint.maxOutputTokens) ||
      checkpoint.maxOutputTokens <= 0 ||
      checkpoint.maxOutputTokens > 2_147_483_647 ||
      checkpoint.maxOutputTokens >= checkpoint.contextWindowTokens
    ) {
      throw new TandemError(
        `Agent '${definition.id}' checkpoint maxOutputTokens must be a positive 32-bit integer smaller than contextWindowTokens.`,
      );
    }
    if (
      !Number.isSafeInteger(checkpoint.checkpointAtPercent) ||
      checkpoint.checkpointAtPercent <= 0 ||
      checkpoint.checkpointAtPercent >= 100
    ) {
      throw new TandemError(
        `Agent '${definition.id}' checkpoint checkpointAtPercent must be between 1 and 99.`,
      );
    }
    if (!capabilities.includes(checkpoint.capability)) {
      throw new TandemError(
        `Agent '${definition.id}' checkpoint capability must be attached to the agent.`,
      );
    }
    requireInstructions(
      checkpoint.instructions,
      `Agent '${definition.id}' checkpoint instructions`,
    );
    if (
      checkpoint.session !== undefined &&
      checkpoint.session !== "retain" &&
      checkpoint.session !== "reset"
    ) {
      throw new TandemError(
        `Agent '${definition.id}' checkpoint session must be 'retain' or 'reset'.`,
      );
    }
    if (
      checkpoint.disableCompaction !== undefined &&
      typeof checkpoint.disableCompaction !== "boolean"
    ) {
      throw new TandemError(
        `Agent '${definition.id}' checkpoint disableCompaction must be a boolean.`,
      );
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
    skills,
    definition.workspace,
    definition.temperature,
    definition.maxOutputTokens,
    definition.continueSession ?? false,
    definition.checkpoint,
    definition.timeoutMs,
  );
}

type ParallelBranches<TState> = Readonly<Record<string, Stage<TState> | Agent<TState>>>;
type ParallelDefinition<TState, TBranches extends ParallelBranches<TState>> = {
  readonly id: string;
  readonly branches: TBranches;
  readonly merge: (
    baseline: TState,
    results: { readonly [K in keyof TBranches]: TState },
  ) => TState;
  readonly persist?: boolean;
};
class ParallelImplementation<TState, TBranches extends ParallelBranches<TState>>
  extends NodeImplementation<TState>
  implements Parallel<TState>
{
  readonly kind = "parallel";
  constructor(
    id: string,
    persist: boolean | undefined,
    readonly branches: TBranches,
    readonly merge: (
      baseline: TState,
      results: { readonly [K in keyof TBranches]: TState },
    ) => TState,
  ) {
    super(id, persist);
  }
}
export function parallel<TState>(): <const TBranches extends ParallelBranches<TState>>(
  definition: ParallelDefinition<TState, TBranches>,
) => Parallel<TState>;
export function parallel<TState, const TBranches extends ParallelBranches<TState>>(
  definition: ParallelDefinition<TState, TBranches>,
): Parallel<TState>;
export function parallel<TState>(
  definition?: ParallelDefinition<TState, ParallelBranches<TState>>,
):
  | Parallel<TState>
  | (<const TBranches extends ParallelBranches<TState>>(
      value: ParallelDefinition<TState, TBranches>,
    ) => Parallel<TState>) {
  if (!definition) {
    return (value) => createParallel(value);
  }
  return createParallel(definition);
}
function createParallel<TState, TBranches extends ParallelBranches<TState>>(
  definition: ParallelDefinition<TState, TBranches>,
): Parallel<TState> {
  const entries = Object.entries(definition.branches);
  if (entries.length < 2) {
    throw new TandemError(`Parallel group '${definition.id}' requires at least two branches.`);
  }
  const participants = new Set(entries.map(([, participant]) => participant));
  if (participants.size !== entries.length) {
    throw new TandemError(
      `Parallel group '${definition.id}' must own a distinct participant per branch.`,
    );
  }
  return new ParallelImplementation(
    definition.id,
    definition.persist,
    definition.branches,
    definition.merge,
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
export interface StandardOutcomeRoute<TState> {
  readonly from: Agent<TState> | Parallel<TState>;
  readonly to: Node<TState>;
  readonly label: string;
  readonly outcome: "success" | "failed";
  readonly when?: (state: TState) => boolean;
}
export type Route<TState> = OrdinaryRoute<TState> | StandardOutcomeRoute<TState>;
export function route<TState>(definition: OrdinaryRoute<TState>): OrdinaryRoute<TState>;
export function route<TState>(
  definition: StandardOutcomeRoute<TState>,
): StandardOutcomeRoute<TState>;
export function route<TState>(definition: Route<TState>): Route<TState> {
  return definition;
}

export interface Pipeline<TState> {
  readonly name: string;
  readonly state: z.ZodType<TState>;
  readonly nodes: readonly Node<TState>[];
  readonly start: Exclude<Node<TState>, Terminal<TState>>;
  readonly routes: readonly Route<TState>[];
  readonly outputs: readonly Terminal<TState>[];
  readonly persist: boolean;
}
export function pipeline<TState>(definition: {
  name: string;
  state: z.ZodType<TState>;
  nodes: readonly Node<NoInfer<TState>>[];
  start: Exclude<Node<NoInfer<TState>>, Terminal<NoInfer<TState>>>;
  routes: readonly Route<NoInfer<TState>>[];
  outputs: readonly Terminal<NoInfer<TState>>[];
  persist?: boolean;
}): Pipeline<TState> {
  const start = definition.start as Node<TState>;
  const members = new Set<Node<TState>>(definition.nodes);
  if (members.size !== definition.nodes.length) {
    throw new Error("Pipeline nodes must contain each participant object exactly once.");
  }
  const ids = new Set(definition.nodes.map((node) => node.id));
  if (ids.size !== definition.nodes.length) {
    throw new Error("Pipeline node IDs must be unique.");
  }
  const ownedParticipants = new Set<Stage<TState> | Agent<TState>>();
  for (const node of definition.nodes) {
    if (node.kind !== "parallel") {
      continue;
    }
    const parallelNode = node as ParallelImplementation<TState, ParallelBranches<TState>>;
    for (const [branchId, participant] of Object.entries(parallelNode.branches)) {
      if (branchId.trim().length === 0) {
        throw new Error(`Parallel group '${node.id}' contains a blank branch ID.`);
      }
      if (members.has(participant)) {
        throw new Error(
          `Parallel branch participant '${participant.id}' cannot also be a parent pipeline node.`,
        );
      }
      if (!ownedParticipants.add(participant)) {
        throw new Error(`Parallel branch participant '${participant.id}' is owned more than once.`);
      }
      if (ids.has(participant.id)) {
        throw new Error(`Pipeline participant ID '${participant.id}' must be globally unique.`);
      }
      ids.add(participant.id);
    }
  }
  if (!members.has(definition.start)) {
    throw new Error(
      `Pipeline start '${definition.start.id}' must be the registered participant object.`,
    );
  }
  if (start.kind === "terminal") {
    throw new Error(`Pipeline start '${start.id}' cannot be a terminal.`);
  }
  for (const item of definition.routes) {
    if (!members.has(item.from) || !members.has(item.to)) {
      throw new Error(`Route '${item.label}' endpoints must be registered participant objects.`);
    }
  }
  const unconditionalRoutes = new Map<string, Route<TState>>();
  for (const item of definition.routes) {
    if (item.when) {
      continue;
    }
    const key = `${item.from.id}\u0000${item.outcome ?? "default"}`;
    const existing = unconditionalRoutes.get(key);
    if (existing) {
      throw new Error(
        `Routes '${existing.label}' and '${item.label}' are both unconditional from '${item.from.id}'.`,
      );
    }
    unconditionalRoutes.set(key, item);
  }
  if (new Set(definition.outputs).size !== definition.outputs.length) {
    throw new Error("Pipeline outputs must contain each terminal exactly once.");
  }
  for (const item of definition.outputs) {
    if (!members.has(item)) {
      throw new Error(`Output '${item.id}' must be the registered participant object.`);
    }
  }
  const reachable = new Set<Node<TState>>([start]);
  const pending: Node<TState>[] = [start];
  while (pending.length > 0) {
    const source = pending.pop()!;
    for (const item of definition.routes) {
      if (item.from === source && !reachable.has(item.to)) {
        reachable.add(item.to);
        pending.push(item.to);
      }
    }
  }
  const outputs = new Set<Node<TState>>(definition.outputs);
  for (const node of reachable) {
    if (node.kind === "terminal" && !outputs.has(node)) {
      throw new Error(`Reachable terminal '${node.id}' must be listed in outputs.`);
    }
  }
  for (const item of definition.outputs) {
    if (!reachable.has(item)) {
      throw new Error(`Output '${item.id}' must be reachable from start '${start.id}'.`);
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
export type RunObservation =
  | { readonly version: 1; readonly kind: "stepStarted"; readonly stepId: string }
  | { readonly version: 1; readonly kind: "stepCompleted"; readonly stepId: string }
  | { readonly version: 1; readonly kind: "stepCancelled"; readonly stepId: string }
  | {
      readonly version: 1;
      readonly kind: "stepFaulted";
      readonly stepId: string;
      readonly error: string;
    }
  | {
      readonly version: 1;
      readonly kind: "agentText";
      readonly stepId: string;
      readonly text: string;
    }
  | {
      readonly version: 1;
      readonly kind: "agentReasoning";
      readonly stepId: string;
      readonly text: string;
    }
  | {
      readonly version: 1;
      readonly kind: "agentModelSelected";
      readonly stepId: string;
      readonly modelId: string;
    }
  | {
      readonly version: 1;
      readonly kind: "agentUsage";
      readonly stepId: string;
      readonly inputTokens: number;
      readonly outputTokens: number;
      readonly currentContextTokens: number;
      readonly contextWindowTokens: number | null;
    }
  | {
      readonly version: 1;
      readonly kind: "structuredOutputRejected";
      readonly stepId: string;
      readonly attempt: number;
      readonly problems: readonly { readonly field: string; readonly message: string }[];
      readonly rawResponse: string;
    };
export interface TerminalPresentationOptions {
  readonly truncatedToolNames?: readonly string[];
}
export interface RunOptions {
  readonly ledgerPath?: string;
  readonly signal?: AbortSignal;
  readonly interactions?: InteractionHandlers;
  readonly presentation?: "terminal";
  readonly terminal?: TerminalPresentationOptions;
  readonly observe?: (
    event: RunObservation,
    context: { readonly signal: AbortSignal },
  ) => void | Promise<void>;
}
const runObservationSchema = z.discriminatedUnion("kind", [
  z
    .object({ version: z.literal(1), kind: z.literal("stepStarted"), stepId: z.string().min(1) })
    .strict(),
  z
    .object({ version: z.literal(1), kind: z.literal("stepCompleted"), stepId: z.string().min(1) })
    .strict(),
  z
    .object({ version: z.literal(1), kind: z.literal("stepCancelled"), stepId: z.string().min(1) })
    .strict(),
  z
    .object({
      version: z.literal(1),
      kind: z.literal("stepFaulted"),
      stepId: z.string().min(1),
      error: z.string(),
    })
    .strict(),
  z
    .object({
      version: z.literal(1),
      kind: z.literal("agentText"),
      stepId: z.string().min(1),
      text: z.string(),
    })
    .strict(),
  z
    .object({
      version: z.literal(1),
      kind: z.literal("agentModelSelected"),
      stepId: z.string().min(1),
      modelId: z.string().min(1),
    })
    .strict(),
  z
    .object({
      version: z.literal(1),
      kind: z.literal("agentReasoning"),
      stepId: z.string().min(1),
      text: z.string(),
    })
    .strict(),
  z
    .object({
      version: z.literal(1),
      kind: z.literal("agentUsage"),
      stepId: z.string().min(1),
      inputTokens: z.number().int().nonnegative(),
      outputTokens: z.number().int().nonnegative(),
      currentContextTokens: z.number().int().nonnegative(),
      contextWindowTokens: z.number().int().nonnegative().nullable(),
    })
    .strict(),
  z
    .object({
      version: z.literal(1),
      kind: z.literal("structuredOutputRejected"),
      stepId: z.string().min(1),
      attempt: z.number().int().positive(),
      problems: z.array(z.object({ field: z.string(), message: z.string().min(1) }).strict()),
      rawResponse: z.string(),
    })
    .strict(),
]);
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
    stepId: z.string().min(1),
    valueType: z.string().min(1).nullable(),
    payload: z.unknown().nullable(),
  })
  .strict()
  .refine((value) => value.valueType !== null || value.payload !== null, {
    message: "valueType and payload cannot both be null",
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
    const { inspectAcceptedAsync } = await import("@maxanstey-meridian/tandem-runtime");
    return parseJson(
      acceptedValuesSchema,
      await inspectAcceptedAsync(options.ledgerPath, options.runId),
      "accepted values",
    ).map(
      (value) =>
        ({
          version: 1 as const,
          kind: value.kind,
          stepId: value.stepId,
          valueType: value.valueType,
          payload: value.payload,
        }) as AcceptedValue,
    );
  } catch (error) {
    if (error instanceof TandemError) {
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
  const initialState = serializeBoundary(graph.state, initial, "initial state");
  if ((graph.persist || graph.nodes.some(participantPersists)) && !options.ledgerPath) {
    throw new TandemError("ledgerPath is required when persistence is enabled.");
  }
  const callbacks = new CallbackRegistry();
  try {
    const nodes = graph.nodes.map((node) => compileNode(node, graph.state, callbacks));
    const routes = graph.routes.map((item) => {
      const callback = item.when
        ? callbacks.registerSync((state) =>
            String(item.when!(parseJson(graph.state, state, `route '${item.label}' state`))),
          )
        : undefined;
      return {
        source: item.from.id,
        target: item.to.id,
        label: item.label,
        outcome: item.outcome,
        predicateCallback: callback,
      };
    });
    const handlerEntries = interactionHandlerEntries(options.interactions);
    const members = new Set(graph.nodes);
    const interactionHandlers = handlerEntries.map((entry, index) => {
      if (!members.has(entry.interaction as Node<TState>)) {
        throw new TandemError(
          `Interaction handler '${entry.interaction.id}' must target a participant in pipeline '${graph.name}'.`,
        );
      }
      const implementation = entry.interaction as InteractionImplementation<
        TState,
        unknown,
        unknown
      >;
      const handleCallback = callbacks.registerAsync(async (_, input, signal) => {
        const response = await entry.handle(
          parseJson(implementation.requestSchema, input, `${implementation.id} request input`),
          { signal },
        );
        signal.throwIfAborted();
        return serializeBoundary(
          implementation.responseSchema,
          response,
          `${implementation.id} response`,
        );
      });
      return { id: `h${index}`, target: entry.interaction.id, handleCallback };
    });
    const observationCallback = options.observe
      ? callbacks.registerAsync(async (_, input, signal) => {
          const event = parseJson(runObservationSchema, input, "run observation");
          const observationSignal =
            options.signal?.aborted === true
              ? AbortSignal.abort(options.signal.reason)
              : event.kind === "stepCancelled" && !signal.aborted
                ? AbortSignal.abort()
                : signal;
          await options.observe!(event, { signal: observationSignal });
          return "";
        })
      : undefined;
    const { runRegisteredGraphAsync } = await import("@maxanstey-meridian/tandem-runtime");
    const resultJson = await runRegisteredGraphAsync(
      JSON.stringify({
        contractVersion: 10,
        name: graph.name,
        start: graph.start.id,
        initialState,
        persist: graph.persist,
        ledgerPath: options.ledgerPath,
        presentation: options.presentation,
        terminal: options.terminal,
        observationCallback,
        nodes,
        routes,
        outputs: graph.outputs.map((item) => item.id),
        interactionHandlers,
      }),
      (id: string, state: string, input: string) => callbacks.invokeSync(id, state, input),
      (id: string, state: string, input: string, signal: AbortSignal) =>
        callbacks.invokeAsync(id, state, input, signal),
      options.signal,
    );
    const result = parseJson(runResultSchema, resultJson, "run result");
    return { ...result, state: parse(graph.state, result.state, "final state") };
  } catch (error) {
    if (error instanceof TandemError) {
      throw error;
    }
    const callbackFailure = callbackContractFailure(error);
    if (callbackFailure) {
      throw new ContractValidationError(callbackFailure.boundary, callbackFailure.problems);
    }
    if (isCancellationError(error, options.signal?.aborted === true)) {
      throw new TandemCancellationError(error);
    }
    throw new TandemRuntimeError("run", error);
  } finally {
    callbacks.dispose();
  }
}

function participantPersists<TState>(node: Node<TState>): boolean {
  const implementation = node as NodeImplementation<TState>;
  if (implementation.persist) {
    return true;
  }
  return implementation instanceof ParallelImplementation
    ? Object.values(implementation.branches).some(
        (branch) => (branch as NodeImplementation<TState>).persist === true,
      )
    : false;
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
    if (error instanceof ContractValidationError) {
      return JSON.stringify(error.problems);
    }
    throw error;
  }
}
const validationProblemsSchema = z.array(
  z.object({ path: z.string(), message: z.string() }).strict(),
);
function validationProblems(problems: readonly ValidationProblem[], boundary: string): string {
  return JSON.stringify(parse(validationProblemsSchema, problems, boundary));
}
function requireInstructions(instructions: string, boundary: string): void {
  if (typeof instructions !== "string" || instructions.trim().length === 0) {
    throw new ContractValidationError(boundary, [
      { path: "$", message: "Instructions must be a non-blank string." },
    ]);
  }
}
function interactionHandlerEntries(
  handlers: InteractionHandlers | undefined,
): readonly RegisteredInteractionHandler[] {
  if (!handlers) {
    return [];
  }
  if (!(handlers instanceof InteractionHandlersImplementation)) {
    throw new TandemError("interactions must be created by interactions().");
  }
  return handlers.entries;
}
function compileNode<TState>(
  node: Node<TState>,
  stateSchema: z.ZodType<TState>,
  callbacks: CallbackRegistry,
): object {
  const implementation = node as NodeImplementation<TState>;
  const base = { id: node.id, persist: implementation.persist };
  if (implementation instanceof StageImplementation) {
    const run = callbacks.registerAsync(async (state, _, signal) =>
      serializeBoundary(
        stateSchema,
        await implementation.execute(parseJson(stateSchema, state, `${node.id} input`), {
          signal,
        }),
        `${node.id} output`,
      ),
    );
    return { ...base, kind: "stage", runCallback: run };
  }
  if (implementation instanceof InteractionImplementation) {
    const request = callbacks.registerSync((state) =>
      serializeBoundary(
        implementation.requestSchema,
        implementation.request(parseJson(stateSchema, state, `${node.id} state`)),
        `${node.id} request`,
      ),
    );
    const apply = callbacks.registerSync((state, input) =>
      serializeBoundary(
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
      applyCallback: apply,
    };
  }
  if (implementation instanceof AgentImplementation) {
    const message = callbacks.registerSync((state) =>
      implementation.message(parseJson(stateSchema, state, `${node.id} message state`)),
    );
    const output = implementation.output
      ? compileAgentOutput(node.id, implementation.output, stateSchema, callbacks)
      : undefined;
    const capabilities = implementation.granted.map((item) =>
      item[compileCapabilityBrand]({ id: node.id, stateSchema, callbacks }),
    );
    const workspace = implementation.workspace
      ? compileWorkspace(node.id, implementation.workspace, stateSchema, callbacks)
      : undefined;
    return {
      ...base,
      kind: "agent",
      instructions: implementation.instructions,
      client: { ...implementation.client, verifyModel: implementation.client.verifyModel ?? false },
      messageCallback: message,
      output,
      capabilities,
      skillDirectories: implementation.skills.map((item) => item.directory),
      temperature: implementation.temperature,
      maxOutputTokens: implementation.maxOutputTokens,
      continueSession: implementation.continueSession,
      checkpoint: implementation.checkpoint
        ? {
            contextWindowTokens: implementation.checkpoint.contextWindowTokens,
            maxOutputTokens: implementation.checkpoint.maxOutputTokens,
            checkpointAtPercent: implementation.checkpoint.checkpointAtPercent,
            capabilityName: implementation.checkpoint.capability.name,
            instructions: implementation.checkpoint.instructions,
            messageCallback: callbacks.registerSync((state, input) =>
              implementation.checkpoint!.message(
                parseJson(stateSchema, state, `${node.id} checkpoint state`),
                Number(input),
              ),
            ),
            resetSession: (implementation.checkpoint.session ?? "reset") === "reset",
            disableCompaction: implementation.checkpoint.disableCompaction ?? false,
          }
        : undefined,
      timeoutMilliseconds: implementation.timeoutMs,
      workspace,
    };
  }
  if (implementation instanceof ParallelImplementation) {
    const entries = Object.entries(implementation.branches) as [
      string,
      Stage<TState> | Agent<TState>,
    ][];
    const branchIds = entries.map(([branchId]) => branchId);
    const mergeCallback = callbacks.registerSync((state, input) => {
      const baseline = parseJson(stateSchema, state, `${node.id} merge baseline`);
      let raw: unknown;
      try {
        raw = JSON.parse(input);
      } catch {
        throw new ContractValidationError(`${node.id} merge branches`, [
          { path: "$", message: "Invalid JSON" },
        ]);
      }
      if (raw === null || typeof raw !== "object" || Array.isArray(raw)) {
        throw new ContractValidationError(`${node.id} merge branches`, [
          { path: "$", message: "Expected a branch-state object." },
        ]);
      }
      const values = raw as Record<string, unknown>;
      if (!isDeepStrictEqual(Object.keys(values).sort(), [...branchIds].sort())) {
        throw new ContractValidationError(`${node.id} merge branches`, [
          { path: "$", message: "Branch-state keys do not match the authored branches." },
        ]);
      }
      const parsed = Object.fromEntries(
        branchIds.map((branchId) => [
          branchId,
          parse(stateSchema, values[branchId], `${node.id} branch '${branchId}' state`),
        ]),
      ) as { readonly [key: string]: TState };
      return serializeBoundary(
        stateSchema,
        implementation.merge(baseline, parsed),
        `${node.id} merged state`,
      );
    });
    return {
      ...base,
      kind: "parallel",
      branches: entries.map(([id, participant]) => ({
        id,
        participant: compileNode(participant, stateSchema, callbacks),
      })),
      mergeCallback,
    };
  }
  const terminal = implementation as TerminalImplementation<TState>;
  const summary = callbacks.registerSync((state) =>
    terminal.summary(parseJson(stateSchema, state, `${node.id} state`)),
  );
  return { ...base, kind: terminal.failed ? "failure" : "completion", summaryCallback: summary };
}

function compileWorkspace<TState>(
  id: string,
  configuration: AgentWorkspaceConfiguration<TState>,
  stateSchema: z.ZodType<TState>,
  callbacks: CallbackRegistry,
): object {
  if (!(configuration instanceof AgentWorkspaceConfigurationImplementation)) {
    throw new TandemError(
      `Agent '${id}' workspace must be created by agentWorkspace().withTools().`,
    );
  }
  const workspace = configuration.workspace;
  const pathCallback = callbacks.registerSync((state) => {
    const value = workspace.path(parseJson(stateSchema, state, `${id} workspace path state`));
    if (typeof value !== "string" || value.trim().length === 0) {
      throw new TandemError(`Agent '${id}' workspace path must be non-blank.`);
    }
    return value;
  });
  const commandsCallback = callbacks.registerSync((state) => {
    const typedState = parseJson(stateSchema, state, `${id} workspace commands state`);
    const value =
      typeof workspace.commandSource === "function"
        ? workspace.commandSource(typedState)
        : (workspace.commandSource ?? []);
    validateAgentCommands(value, `Agent '${id}' workspace commands`);
    return serializeBoundary(
      z.array(
        z
          .object({
            name: z.string(),
            description: z.string(),
            command: z.string(),
            arguments: z
              .array(
                z
                  .object({
                    name: z.string(),
                    description: z.string(),
                    flag: z.string(),
                    pattern: z.string().optional(),
                    allowedValues: z.array(z.string()).optional(),
                    maxLength: z.number().optional(),
                  })
                  .strict(),
              )
              .optional(),
          })
          .strict(),
      ),
      value,
      `${id} workspace commands`,
    );
  });
  const selected = new Set<AgentToolSelection>();
  const toolGroups = configuration.groups.map((group, index) => {
    let includeCommands = false;
    const tools: AgentToolName[] = [];
    for (const tool of group.tools) {
      if (typeof tool === "string") {
        if (selected.has(tool)) {
          throw new TandemError(`Agent '${id}' selects '${tool}' more than once.`);
        }
        selected.add(tool);
        tools.push(tool);
      } else {
        if (tool[commandSelectionBrand] !== workspace) {
          throw new TandemError(`Agent '${id}' selects commands from another workspace.`);
        }
        if (workspace.commandSource === undefined) {
          throw new TandemError(
            `Agent '${id}' selects workspace commands without declaring a command catalogue.`,
          );
        }
        if (selected.has(tool)) {
          throw new TandemError(`Agent '${id}' selects workspace commands twice.`);
        }
        selected.add(tool);
        includeCommands = true;
      }
    }
    const whenCallback = group.predicate
      ? callbacks.registerSync((state) => {
          const value = group.predicate!(
            parseJson(stateSchema, state, `${id} tool group ${index} state`),
          );
          if (typeof value !== "boolean") {
            throw new TandemError(
              `Agent '${id}' tool group ${index} predicate must return a boolean.`,
            );
          }
          return String(value);
        })
      : undefined;
    return { tools, includeCommands, whenCallback };
  });
  const interceptCallback = configuration.interceptTool
    ? callbacks.registerAsync(async (state, input, signal) => {
        const typedState = parseJson(stateSchema, state, `${id} tool interception state`);
        let invocation: AgentToolInvocation;
        try {
          invocation = JSON.parse(input) as AgentToolInvocation;
        } catch {
          throw new TandemError(`Agent '${id}' received an invalid tool interception payload.`);
        }
        const result = await configuration.interceptTool!(typedState, invocation, { signal });
        if (result !== null && typeof result !== "string") {
          throw new TandemError(`Agent '${id}' tool interceptor must return a string or null.`);
        }
        return JSON.stringify(result);
      })
    : undefined;
  return { pathCallback, commandsCallback, toolGroups, interceptCallback };
}

function compileAgentOutput<TState, TOutput>(
  id: string,
  output: {
    instructions: string;
    schema: z.ZodType<TOutput>;
    validateFor?: (state: TState, output: TOutput) => readonly ValidationProblem[];
    apply: (state: TState, output: TOutput) => TState;
  },
  stateSchema: z.ZodType<TState>,
  callbacks: CallbackRegistry,
): object {
  const validate = callbacks.registerSync((_, input) => issues(output.schema, input));
  const validateFor = output.validateFor
    ? callbacks.registerSync((state, input) =>
        validationProblems(
          output.validateFor!(
            parseJson(stateSchema, state, `${id} state`),
            parseJson(output.schema, input, `${id} output`),
          ),
          `${id} output contextual validation`,
        ),
      )
    : undefined;
  const apply = callbacks.registerSync((state, input) =>
    serializeBoundary(
      stateSchema,
      output.apply(
        parseJson(stateSchema, state, `${id} state`),
        parseJson(output.schema, input, `${id} output`),
      ),
      `${id} applied state`,
    ),
  );
  return {
    instructions: output.instructions,
    jsonSchema: inputJsonSchema(output.schema, `${id} output schema`),
    validateCallback: validate,
    validateForCallback: validateFor,
    applyCallback: apply,
    valueType: `${id}.output`,
  };
}
