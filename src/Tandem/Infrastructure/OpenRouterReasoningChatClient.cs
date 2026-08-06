using System.ClientModel.Primitives;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using StreamingChatCompletionUpdate = OpenAI.Chat.StreamingChatCompletionUpdate;

namespace Tandem.Infrastructure;

#pragma warning disable SCME0001

internal sealed class OpenRouterReasoningChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private static readonly PropertyInfo? _patchProperty =
        typeof(StreamingChatCompletionUpdate).GetProperty(
            "Patch",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (
            var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
        )
        {
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
