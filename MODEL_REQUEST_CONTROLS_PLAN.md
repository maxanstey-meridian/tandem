# Model Request Controls Plan

## Status

Implemented against `main` with one shared C# model-request policy and TypeScript
translation through that public API. The combined TypeScript registration
contract is version 9.

## Outcome

Tandem C# and TypeScript agents can configure three general model-request controls:

- explicitly disable or select reasoning effort;
- set temperature;
- set maximum output tokens.

```ts
const client = {
  kind: "openai-compatible",
  version: 1,
  endpoint,
  model,
  wireApi: "completions",
  reasoningEffort: "none",
} as const;

const world = agent({
  id: "world",
  client,
  temperature: 0,
  maxOutputTokens: 4096,
  instructions: "Return one playable world.",
  message: (state) => state.request,
  output: worldOutput,
});
```

The generated OpenAI-compatible request must preserve all configured controls
and any structured JSON response format on the same request.

Omitting a control means no Tandem preference. Tandem must not synthesize a
default value when the application leaves a control undefined.

## Scope

Version one supports:

- `reasoningEffort: "none" | "low" | "medium" | "high"` on the existing
  OpenAI-compatible client descriptor;
- `temperature` on a TypeScript agent;
- `maxOutputTokens` on a TypeScript agent;
- both `completions` and `responses` wire APIs through the existing maintained
  OpenAI/M.E.AI adapters;
- structured-output requests;
- structured-output correction requests;
- agents nested inside parallel groups;
- exact registration-boundary validation;
- near-metal captured-wire tests;
- explicit live provider characterization.

Version one deliberately excludes:

- arbitrary provider JSON;
- additional-property escape hatches;
- provider-specific request dictionaries;
- top-p, top-k, seed, penalties, stop sequences, or other sampling controls;
- model-specific default inference;
- silently translating unsupported values;
- treating omitted reasoning effort as disabled reasoning;

The implementation must stop rather than add an arbitrary JSON escape hatch if
the maintained adapter cannot express a provider's coherent disable form.

## Existing Tandem Concepts

### TypeScript chat-client descriptor

`typescript/packages/sdk/src/index.ts` defines
`OpenAiCompatibleChatClient` around lines 584-593. It currently includes:

```ts
readonly reasoningEffort?: "low" | "medium" | "high";
```

Reasoning effort is part of the client descriptor because the bridge builds one
OpenAI-compatible `IChatClient` for each registered agent in
`RegisteredParticipantFactory.CreateAgentAsync`. Reusing the same TypeScript
descriptor value across agents still creates separate runtime clients.

Extend the type to:

```ts
readonly reasoningEffort?: "none" | "low" | "medium" | "high";
```

### TypeScript agent definition

`typescript/packages/sdk/src/index.ts` defines `AgentDefinition<TState,
TOutput>` around lines 604-620 and stores the authored values in
`AgentImplementation<TState, TOutput>` around lines 621-647.

Add:

```ts
readonly temperature?: number;
readonly maxOutputTokens?: number;
```

These are per-agent request controls. They must be retained by
`AgentImplementation`, validated in `agent(...)`, and emitted by `compileNode`.

### Registration contract

`typescript/bridge/RegistrationContract.cs` defines
`RegisteredNodeContract`. Temperature and maximum output tokens belong on agent
nodes, not on `RegisteredChatClientContract`, because they are agent request
controls and must compose with agent output configuration.

Add nullable fields:

```csharp
double? Temperature,
int? MaxOutputTokens,
```

`RegisteredChatClientContract.ReasoningEffort` already carries reasoning effort.

The registration protocol is lockstep: the SDK emits one schema and the bundled
runtime accepts exactly that schema. Adding these fields requires the next
combined contract version. Do not independently claim the same version as
concurrent workspace-tool work. If that work has already moved version 7 to
version 8, this feature should produce the next combined version rather than a
second incompatible version 8.

### Bridge participant construction

`typescript/bridge/RegisteredParticipants.cs` constructs the Core
`AgentBuilder<JavaScriptState>` in `CreateAgentAsync` around lines 119-180.

The bridge:

1. creates the client and builder;
2. attaches skills;
3. configures structured output;
4. attaches capabilities and remaining agent options.

Translate the complete registered policy through the public C# API:

```csharp
builder.WithModelRequestOptions(
    new AgentModelRequestOptions(reasoningEffort, temperature, maxOutputTokens));
```

The bridge must not configure `ChatOptions` or wrap `IChatClient` to own these
semantics independently.

### Core ChatOptions creation

`src/Tandem/Infrastructure/Blocks/AgentBlock.cs` creates a `ChatOptions` object
in `CreateAgent` around lines 855-877.

The existing shape is:

```csharp
var chatOptions = new ChatOptions
{
    Instructions = ...,
    Tools = ...,
};

if (configureStructuredOutput)
{
    configureChatOptions?.Invoke(chatOptions);
}
```

The `configureStructuredOutput` flag is deliberately false on the required-tool
structured-output correction path around lines 353-361. That correction removes
the response format while forcing the acceptance tool.

General request controls must still apply on that correction request. Therefore,
temperature and maximum output tokens cannot share the conditional structured
output delegate.

### OpenAI-compatible client adapter

`typescript/bridge/OpenAiCompatibleChatClients.cs` builds either:

- `OpenAIChatClient` for `wireApi: "completions"`; or
- `OpenAIResponsesChatClient` for `wireApi: "responses"`.

It wraps the client with configured M.E.AI `ReasoningOptions` around lines 46-63.

Extend its switch:

```csharp
"none" => ReasoningEffort.None,
"low" => ReasoningEffort.Low,
"medium" => ReasoningEffort.Medium,
"high" => ReasoningEffort.High,
```

Do not patch raw request JSON in `OpenAiCompatibleChatClients`.

`src/Tandem.OpenAICompatible/OpenRouterReasoningChatClient.cs` is a response-side
adapter. It extracts OpenRouter reasoning deltas and removes empty text content.
It does not alter request reasoning settings and should not need to change.

## Maintained Adapter Evidence

The installed `Microsoft.Extensions.AI.Abstractions` 10.8.3 package defines:

```csharp
ReasoningEffort.None
ReasoningEffort.Low
ReasoningEffort.Medium
ReasoningEffort.High
ReasoningEffort.ExtraHigh
```

The installed M.E.AI OpenAI adapter maps chat-completions reasoning through:

```csharp
ReasoningEffort.None => ChatReasoningEffortLevel.None
```

The resulting standard chat-completions field is:

```json
"reasoning_effort": "none"
```

The same adapter maps:

```csharp
ChatOptions.Temperature
    -> ChatCompletionOptions.Temperature

ChatOptions.MaxOutputTokens
    -> ChatCompletionOptions.MaxOutputTokenCount
```

For the installed OpenAI SDK, the chat-completions token field serializes as:

```json
"max_completion_tokens": 4096
```

The Responses adapter maps the same M.E.AI controls to the Responses request
shape, including its response-format representation. Tandem should rely on that
adapter rather than own API-specific field names.

## Core C# Design

### Public request policy

The C# authoring API owns the provider-neutral request semantics:

```csharp
new AgentModelRequestOptions(
    reasoningEffort: AgentReasoningEffort.None,
    temperature: 0,
    maxOutputTokens: 4096)
```

`AgentBuilder<TState>.WithModelRequestOptions(...)` applies that policy to every
model turn. The TypeScript bridge constructs the same public value object rather
than owning a bridge-only options configurator.

Existing output methods continue to own only response format:

- `WithOutput<TOutput>`;
- `WithJsonOutput`;
- `ConfigureStructuredOutput`;
- `ConfigureOutput<TOutput>`.

Do not combine response-format configuration with model request controls. Their
different correction-turn lifecycles require separate delegates.

### AgentBlock application order

`AgentBlock.CreateAgent` should apply options in this order:

```csharp
configureModelRequestOptions?.Invoke(chatOptions);

if (configureStructuredOutput)
{
    configureStructuredOutputOptions?.Invoke(chatOptions);
}
```

This guarantees:

- ordinary requests receive generation controls;
- structured-output requests receive generation controls and response format;
- required-tool correction requests retain generation controls while omitting
  response format as intended;
- later output configuration cannot erase temperature or token limits.

Carry both delegates through the smallest existing internal seam. Do not
introduce provider-specific fields into `TState`; request policy belongs to the
agent definition and wire serialization remains in maintained adapters.

## TypeScript Validation

### Reasoning effort

Accept exactly:

```text
none
low
medium
high
```

Omission remains valid and means no preference.

Reject arbitrary strings at both TypeScript authoring and bridge validation.
TypeScript compile-time narrowing is not sufficient because JavaScript and raw
registration JSON can bypass it.

### Temperature

Require:

- JavaScript type `number`;
- `Number.isFinite(value)`;
- `0 <= value <= 2`.

The OpenAI-compatible descriptor promises the OpenAI-compatible range. A server
or model may still reject temperature for a specific model; that provider error
must remain visible rather than causing Tandem to silently omit the authored
value.

Temperature `0` is valid and must not be treated as missing by truthiness checks.

### Maximum output tokens

Require:

- JavaScript type `number`;
- `Number.isSafeInteger(value)`;
- `value > 0`;
- `value <= int.MaxValue` (`2_147_483_647`).

The bridge contract should use `int?`. Fractional token counts are invalid even
though JSON represents them as numbers.

### Node ownership

`temperature` and `maxOutputTokens` must:

- be required nullable fields on agent nodes if the registration contract uses
  exact nullable shape;
- be forbidden on ordinary stages;
- be forbidden on interactions;
- be forbidden on completion and failure terminals;
- be forbidden on parallel container nodes;
- be permitted on agent participants nested inside parallel branches.

## Registration Compilation

Extend `AgentImplementation` to retain both values:

```ts
readonly temperature: number | undefined;
readonly maxOutputTokens: number | undefined;
```

`agent(...)` passes the validated authored values to the implementation.

`compileNode` emits:

```ts
temperature: implementation.temperature ?? null,
maxOutputTokens: implementation.maxOutputTokens ?? null,
```

Preserve `0` explicitly. Do not use `|| null`.

Recursive parallel compilation already calls the same agent compilation path;
tests must prove nested agents carry the fields rather than assuming that
property.

## Bridge Validation

Update `typescript/bridge/RegistrationContractValidator.cs`:

1. Accept `"none"` in the chat-client reasoning-effort set.
2. Validate finite temperature in the inclusive range `[0, 2]`.
3. Validate positive integer maximum output tokens.
4. Enforce field ownership by agent nodes.
5. Preserve recursive validation for parallel branch participants.

`System.Text.Json` normally rejects nonstandard JSON `NaN` and infinity tokens,
but C# validation should still call `double.IsFinite` because contracts may be
constructed directly in bridge tests.

## Test Plan

### Type tests

Update `typescript/tests/types/positive/facade.ts` with:

```ts
reasoningEffort: "none",
temperature: 0,
maxOutputTokens: 4096,
```

Update the negative fixture to reject:

- unknown reasoning effort;
- string temperature;
- string token count;
- structurally misplaced controls where compile-time typing can express the
  error.

### TypeScript authoring tests

Add runtime authoring tests for:

- temperature `0` accepted;
- temperature `2` accepted;
- negative temperature rejected;
- temperature above `2` rejected;
- `NaN` rejected;
- positive and negative infinity rejected;
- positive integer output tokens accepted;
- zero rejected;
- negative rejected;
- fractional values rejected;
- values above `int.MaxValue` rejected.

### Registration validator tests

Update `typescript/bridge-tests/RegistrationContractValidatorTests.cs` to cover:

- valid agent controls;
- nullable omitted controls;
- `"none"` reasoning;
- invalid reasoning strings;
- temperature range and finiteness;
- positive integer maximum tokens;
- non-agent field rejection;
- nested parallel agent acceptance;
- the combined contract version.

### Compilation test

Compile a TypeScript graph and inspect registration JSON. Assert exact values:

```json
{
  "temperature": 0,
  "maxOutputTokens": 4096,
  "client": {
    "reasoningEffort": "none"
  }
}
```

Include one nested parallel agent in this test or a dedicated recursive test.

### Captured-wire test

Extend the real local HTTP fixture in:

- `typescript/tests/openai-server-child.mjs`;
- its associated runtime child/test.

Use a structured-output agent with:

```ts
reasoningEffort: "none",
temperature: 0,
maxOutputTokens: 4096,
```

Capture the actual request after it has passed through:

```text
TS SDK
-> registration JSON
-> bridge validator
-> Core AgentBuilder
-> AgentBlock ChatOptions
-> M.E.AI OpenAI adapter
-> OpenAI SDK serialization
-> HTTP server
```

For chat completions, assert:

```json
{
  "reasoning_effort": "none",
  "temperature": 0,
  "max_completion_tokens": 4096,
  "response_format": {
    "type": "json_schema"
  }
}
```

Assert the actual nested schema rather than only the outer response-format type.

Add or extend the Responses fixture to assert its corresponding maintained SDK
shape rather than assuming chat-completions field names.

### Correction-turn regression

Exercise a structured-output failure whose correction requires an acceptance
tool and therefore calls `CreateAgent(..., configureStructuredOutput: false)`.

Assert the correction request:

- retains `temperature: 0`;
- retains the maximum output token limit;
- retains explicit reasoning disable through the client wrapper;
- omits structured response format as intended;
- requires the configured correction tool.

This test protects the reason generation controls need a separate Core delegate.

### OpenRouter adapter regression

Existing bridge tests prove that OpenRouter chat-completions endpoints receive
`OpenRouterReasoningChatClient`. Add a captured request assertion proving
`reasoning_effort: "none"` reaches the inner request unchanged while the
response-side adapter remains installed.

## Live Provider Characterization

### Qwen result

The configured Qwen llama.cpp server was discovered over SSH on the Studio:

```text
endpoint: http://127.0.0.1:8000/v1
model: unsloth/Qwen3.6-35B-A3B-MTP-GGUF:UD-Q4_K_XL
```

It was launched with:

```text
--reasoning off
```

A live request containing:

```json
{
  "reasoning_effort": "none",
  "temperature": 0,
  "max_completion_tokens": 4
}
```

was accepted. The response emitted no reasoning content and stopped at exactly
four completion tokens with `finish_reason: "length"`.

This confirms:

- llama.cpp accepts the standard explicit-disable field;
- temperature zero is accepted;
- the exact `max_completion_tokens` form emitted by the installed adapter is
  honored;
- no reasoning is emitted under the current deployment.

It does not prove that the request itself disabled reasoning because the server
was globally launched with `--reasoning off`. A strict behavioral comparison
requires a temporary Qwen instance without that global flag and two otherwise
identical requests: omitted reasoning effort and `"none"`.

The current result is sufficient to proceed past the compatibility stop
condition. Record the global-flag caveat in any final characterization test or
release note.

### DS4/OpenRouter requirement

The DS4/OpenRouter live probe remains outstanding.

The probe must use the actual intended OpenRouter endpoint/model and capture:

- serialized `reasoning_effort: "none"`;
- temperature zero;
- maximum output token field;
- structured response format;
- absence of reasoning deltas/content;
- bounded output completion rather than reasoning-token runaway.

Do not claim DS4 support from the local `:8080` server or from a mock OpenRouter
hostname. Use the actual provider route and model configuration.

If OpenRouter accepts the request but still emits reasoning, stop. Investigate a
coherent named provider-adapter policy. Do not patch arbitrary JSON at the agent
call site.

## Files To Change

Expected implementation files:

- `typescript/packages/sdk/src/index.ts`
- `typescript/bridge/RegistrationContract.cs`
- `typescript/bridge/RegistrationContractValidator.cs`
- `typescript/bridge/RegisteredParticipants.cs`
- `typescript/bridge/OpenAiCompatibleChatClients.cs`
- `src/Tandem/Authoring/AgentSdk.cs`
- `src/Tandem/ExportedApi.txt`
- `src/Tandem/PublicApiMembers.txt`
- `src/Tandem/Domain/AgentBlockConfig.cs` if delegates are carried separately
  there
- `src/Tandem/Infrastructure/Blocks/AgentBlock.cs`
- TypeScript type/runtime fixtures
- bridge validator and adapter tests
- relevant public/internal contract documentation

Do not change `OpenRouterReasoningChatClient` unless a failing response-side test
demonstrates a distinct bug.

## Implementation Sequence

1. Rebase the plan against the completed workspace-tool HEAD and select one
   combined registration contract version.
2. Add the public C# request policy and `AgentBuilder` method.
3. Split Core structured-output and model-request configuration.
4. Apply model request controls unconditionally in `AgentBlock`.
5. Add TS types and authoring validation.
6. Extend `AgentImplementation` and registration compilation.
7. Extend the bridge node contract and validation.
8. Translate bridge fields through `AgentModelRequestOptions`.
9. Add type, authoring, contract, recursive compilation, and native correction
   tests.
10. Add captured chat-completions and Responses wire assertions.
11. Run the Qwen characterization through the implemented Tandem path, not only
    direct curl.
12. Run the DS4/OpenRouter live probe.
13. Update TypeScript API documentation and contract version references.
14. Run formatting, all .NET tests, all TypeScript tests, package verification,
    packed-consumer tests, and `~/Sites/plumb/plumb . --json`.

## Acceptance Criteria

The feature is complete when:

- TypeScript accepts reasoning `"none"`, temperature zero, and a positive token
  limit;
- invalid numeric values fail before model execution;
- registration JSON carries exact values without losing zero;
- only agent nodes may own temperature and maximum output tokens;
- nested parallel agents retain the controls;
- Core composes generation controls with structured output;
- correction turns retain generation controls while intentionally dropping
  response format;
- M.E.AI emits standard provider wire fields without Tandem JSON patching;
- Qwen accepts the implemented request path and honors the token limit;
- DS4/OpenRouter emits no reasoning and remains bounded;
- no arbitrary JSON escape hatch exists;
- all repository and Meridian mechanical gates pass.

## Stop Conditions

Stop implementation and return to provider-adapter design if any of these occur:

- `ReasoningEffort.None` does not serialize through the maintained adapter;
- Qwen rejects the maintained serialization form;
- OpenRouter strips or rewrites explicit disable;
- DS4 emits reasoning despite explicit disable;
- preserving structured output requires raw request mutation;
- provider support requires exposing arbitrary request JSON publicly;
- concurrent tool work has created an incompatible contract version that cannot
  be combined cleanly.
