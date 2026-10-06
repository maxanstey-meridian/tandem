# Chat Clients

Every agent needs a model client. The host chooses which model performs each role, so the same
pipeline can run against different providers.

## C#

An agent takes any `Microsoft.Extensions.AI` `IChatClient`. For an OpenAI-compatible endpoint
such as OpenRouter:

```csharp
using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI;
using Tandem.OpenAICompatible;

var openAi = new OpenAIClient(
    new ApiKeyCredential(apiKey),
    new OpenAIClientOptions
    {
        Endpoint = new Uri("https://openrouter.ai/api/v1/"),
        // StreamRetryChatClient below is the only retry layer.
        RetryPolicy = new ClientRetryPolicy(0),
    });

IChatClient client = new StreamRetryChatClient(
    new ReasoningExtractionChatClient(
        openAi.GetChatClient("deepseek/deepseek-v4-flash-0731").AsIChatClient()));
```

`Meridian.Tandem.OpenAICompatible` provides three optional wrappers:

| Wrapper | Does |
|---|---|
| `ReasoningExtractionChatClient` | Turns streamed reasoning that providers put in non-standard fields, such as OpenRouter's `delta.reasoning`, into standard reasoning content that observers and the terminal can show |
| `StreamRetryChatClient` | Buffers each response and retries transport failures and HTTP 408, 429 and 5xx, up to four attempts, after 1, 2 and 4 seconds. Cancellation is never retried |
| `RequestDeadlineChatClient` | Bounds each attempt by a total deadline and a streaming inactivity deadline. Put it inside `StreamRetryChatClient`, so a timeout is retried |

`StreamRetryChatClient` should be the **only** retry layer. Build the OpenAI client with
`RetryPolicy = new ClientRetryPolicy(0)`. Otherwise the SDK's default three retries multiply
every attempt. Buffering keeps completed agent and tool turns intact, but it delays streamed
updates until each model response completes.

## TypeScript

A client is a plain object:

```ts
import type { ChatClient } from "@maxanstey-meridian/tandem";

const client = {
  kind: "openai-compatible",
  version: 1,
  endpoint: "https://openrouter.ai/api/v1",
  model: "deepseek/deepseek-v4-flash-0731",
  wireApi: "completions",
  apiKeyEnvironmentVariable: "OPENROUTER_API_KEY",
} as const satisfies ChatClient;
```

| Field | Meaning |
|---|---|
| `endpoint` | The OpenAI-compatible base URL |
| `model` | The model ID |
| `wireApi` | `"completions"` or `"responses"` |
| `apiKeyEnvironmentVariable` | The variable that holds the key. The key itself never passes through your code. Omit it only for a local, keyless endpoint |
| `verifyModel` | Check that the endpoint lists the model before the run starts |
| `requestTimeoutMs`, `idleTimeoutMs`, `maxAttempts` | Per-attempt transport limits. Omit them to keep the provider defaults |

The bridge wraps every TypeScript client in the same `StreamRetryChatClient`, with the OpenAI
SDK's own retries disabled. A client without a key may only call a loopback host.
