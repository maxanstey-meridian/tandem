using Microsoft.Extensions.AI;

namespace Tandem.Infrastructure.Blocks;

/// <summary>
/// Normalizes outgoing request history so every tool result sits adjacent to the
/// assistant message that issued the matching function call. Session composition
/// (for example, a capability result injected after the next invocation's user
/// message) can order a tool result away from its call; providers following the
/// OpenAI wire convention reject such histories outright.
/// </summary>
internal sealed class ToolResultAdjacencyChatClient(IChatClient innerClient)
    : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => InnerClient.GetResponseAsync(Normalize(messages), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default
    ) => InnerClient.GetStreamingResponseAsync(Normalize(messages), options, cancellationToken);

    public override object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ToolResultAdjacencyChatClient)
            ? this
            : InnerClient.GetService(serviceType, serviceKey);

    internal static IReadOnlyList<ChatMessage> Normalize(IEnumerable<ChatMessage> messages)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        var callOwner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Role != ChatRole.Assistant)
            {
                continue;
            }
            foreach (var call in list[i].Contents.OfType<FunctionCallContent>())
            {
                if (call.CallId is not null)
                {
                    callOwner[call.CallId] = i;
                }
            }
        }

        var misplaced = new List<(int Index, int OwnerIndex)>();
        var orphaned = new List<int>();
        var hasConversationContext = list.Any(message => message.Role != ChatRole.Tool);
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Role != ChatRole.Tool)
            {
                continue;
            }
            var callIds = list[i]
                .Contents.OfType<FunctionResultContent>()
                .Select(result => result.CallId)
                .Where(id => id is not null)
                .ToHashSet(StringComparer.Ordinal);
            if (callIds.Count == 0)
            {
                continue;
            }
            var ownerIndex = int.MaxValue;
            foreach (var callId in callIds)
            {
                if (!callOwner.TryGetValue(callId!, out var owner))
                {
                    ownerIndex = -1;
                    break;
                }
                ownerIndex = Math.Min(ownerIndex, owner);
            }
            if (ownerIndex < 0)
            {
                if (hasConversationContext)
                {
                    orphaned.Add(i);
                }
                continue;
            }
            if (!IsAdjacentTo(list, i, ownerIndex, callOwner))
            {
                misplaced.Add((i, ownerIndex));
            }
        }

        if (misplaced.Count == 0 && orphaned.Count == 0)
        {
            return list;
        }

        var reordered = list.ToList();
        foreach (var index in orphaned.OrderDescending())
        {
            reordered.Remove(list[index]);
        }
        foreach (var (index, ownerIndex) in misplaced.OrderBy(item => item.Index))
        {
            var message = list[index];
            reordered.RemoveAt(reordered.FindIndex(m => ReferenceEquals(m, message)));
            var owner = list[ownerIndex];
            var insertAt = reordered.FindIndex(m => ReferenceEquals(m, owner)) + 1;
            while (
                insertAt < reordered.Count
                && reordered[insertAt].Role == ChatRole.Tool
                && OwnsAny(reordered[insertAt], callOwner, ownerIndex)
            )
            {
                insertAt++;
            }
            reordered.Insert(insertAt, message);
        }
        return reordered;

        static bool IsAdjacentTo(
            IReadOnlyList<ChatMessage> list,
            int index,
            int ownerIndex,
            Dictionary<string, int> callOwner
        )
        {
            var previous = index - 1;
            while (previous >= 0)
            {
                if (list[previous].Role != ChatRole.Tool)
                {
                    return previous == ownerIndex;
                }
                if (
                    !list[previous]
                        .Contents.OfType<FunctionResultContent>()
                        .Any(result =>
                            result.CallId is not null
                            && callOwner.TryGetValue(result.CallId, out var owner)
                            && owner == ownerIndex
                        )
                )
                {
                    return false;
                }
                previous--;
            }
            return false;
        }

        static bool OwnsAny(ChatMessage message, Dictionary<string, int> owners, int ownerIndex) =>
            message
                .Contents.OfType<FunctionResultContent>()
                .Any(result =>
                    result.CallId is not null
                    && owners.TryGetValue(result.CallId, out var owner)
                    && owner == ownerIndex
                );
    }
}
