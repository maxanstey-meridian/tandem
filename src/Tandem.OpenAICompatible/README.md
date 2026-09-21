# Meridian.Tandem.OpenAICompatible

Optional provider normalization for Tandem applications using OpenAI-compatible endpoints.

```sh
dotnet add package Meridian.Tandem.OpenAICompatible --version 0.1.0
```

`ReasoningExtractionChatClient` preserves streamed reasoning that upstreams emit in
non-standard fields such as OpenRouter's `delta.reasoning` as standard
`TextReasoningContent`, allowing Tandem observers and terminal presentation to receive it.

`StreamRetryChatClient` buffers each response and retries transport failures up to
four attempts, with 1, 2 and 4 second delays. Partial failed responses are discarded;
cancellation and non-transport failures are not retried. The TypeScript bridge
uses this wrapper for its OpenAI-compatible clients, including Responses. This
preserves completed agent/tool turns but delays streamed updates until each model
response completes. Native C# clients can opt in by wrapping their `IChatClient`.

RequestDeadlineChatClient optionally bounds each attempt by a total deadline and
a streaming inactivity deadline. Place it inside StreamRetryChatClient so timeout
exceptions retry the request, while caller cancellation never retries. TypeScript
clients expose requestTimeoutMs, idleTimeoutMs and maxAttempts; omitted values
preserve existing behaviour.
