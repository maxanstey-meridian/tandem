using System.ClientModel.Primitives;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ChatCompletionOptions = OpenAI.Chat.ChatCompletionOptions;
using StreamingChatCompletionUpdate = OpenAI.Chat.StreamingChatCompletionUpdate;

namespace Tandem.OpenAICompatible;

#pragma warning disable SCME0001

public sealed class OpenRouterReasoningChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private const string ReasoningMaxTokensKey = "reasoningMaxTokens";
    private static readonly PropertyInfo? _patchProperty =
        typeof(StreamingChatCompletionUpdate).GetProperty(
            "Patch",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ConfigureReasoningBudget(options);
        await using var updates = base.GetStreamingResponseAsync(
                messages,
                options,
                cancellationToken
            )
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await updates.MoveNextAsync())
                {
                    yield break;
                }

                update = updates.Current;
            }
            catch (ArgumentOutOfRangeException exception)
                when (exception.ParamName == "value" && Equals(exception.ActualValue, "error"))
            {
                throw new HttpRequestException(
                    "OpenRouter terminated the streaming response with a provider error.",
                    exception
                );
            }

            if (
                !update.Contents.OfType<TextReasoningContent>().Any()
                && TryExtractReasoning(update.RawRepresentation, out var reasoning)
            )
            {
                update.Contents.Add(new TextReasoningContent(reasoning));
            }

            for (var index = update.Contents.Count - 1; index >= 0; index--)
            {
                if (update.Contents[index] is TextContent { Text.Length: 0 })
                {
                    update.Contents.RemoveAt(index);
                }
            }

            yield return update;
        }
    }

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    )
    {
        ConfigureReasoningBudget(options);
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    internal static void ConfigureReasoningBudget(ChatOptions? options)
    {
        if (
            options?.AdditionalProperties?.TryGetValue(ReasoningMaxTokensKey, out var value) != true
            || value is not int maxTokens
        )
        {
            return;
        }

        var existingFactory = options.RawRepresentationFactory;
        options.RawRepresentationFactory = client =>
        {
            var raw =
                existingFactory?.Invoke(client) as ChatCompletionOptions
                ?? new ChatCompletionOptions();
            raw.Patch.Set(
                "$.reasoning.max_tokens"u8,
                JsonSerializer.SerializeToUtf8Bytes(maxTokens)
            );
            return raw;
        };
    }

    internal static bool TryExtractReasoning(object? raw, out string reasoning)
    {
        reasoning = "";
        if (
            raw is not StreamingChatCompletionUpdate streaming
            || _patchProperty?.GetValue(streaming) is not JsonPatch patch
        )
        {
            return false;
        }

        return TryExtractReasoning(patch, out reasoning);
    }

    internal static bool TryExtractReasoning(JsonPatch patch, out string reasoning)
    {
        if (
            patch.TryGetValue("$.choices[0].delta.reasoning"u8, out string? value)
            && !string.IsNullOrEmpty(value)
        )
        {
            reasoning = value;
            return true;
        }

        reasoning = "";
        return false;
    }
}

#pragma warning restore SCME0001
