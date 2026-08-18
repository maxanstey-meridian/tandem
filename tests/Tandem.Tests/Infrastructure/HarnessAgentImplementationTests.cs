using FluentAssertions;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Tests.Infrastructure;

public sealed class HarnessAgentImplementationTests
{
    [Fact]
    public void Checkpoint_limits_enable_framework_in_loop_compaction()
    {
        var options = HarnessAgentImplementation.CreateOptions(
            Context(200_000, 32_000),
            "Harness."
        );

        options.MaxContextWindowTokens.Should().Be(200_000);
        options.MaxOutputTokens.Should().Be(32_000);
        options.DisableCompaction.Should().BeFalse();
        options.CompactionStrategy.Should().NotBeNull();
    }

    [Fact]
    public void Agents_without_checkpoint_limits_do_not_invent_compaction_limits()
    {
        var options = HarnessAgentImplementation.CreateOptions(Context(null, null), "Harness.");

        options.MaxContextWindowTokens.Should().BeNull();
        options.MaxOutputTokens.Should().BeNull();
        options.DisableCompaction.Should().BeTrue();
        options.CompactionStrategy.Should().BeNull();
    }

    [Fact]
    public void Compaction_strategy_uses_fixed_summary_instead_of_default_formatter()
    {
        var options = HarnessAgentImplementation.CreateOptions(
            Context(200_000, 32_000),
            "Harness."
        );

        var pipeline = options.CompactionStrategy.Should().BeOfType<PipelineCompactionStrategy>().Subject;
        var strategies = pipeline.Strategies;
        var toolResult = strategies
            .Should()
            .ContainSingle(s => s is ToolResultCompactionStrategy)
            .Which.Should()
            .BeOfType<ToolResultCompactionStrategy>()
            .Subject;

        toolResult.ToolCallFormatter.Should().NotBeNull();
        toolResult.MinimumPreservedGroups.Should().Be(2);
    }

    private static AgentImplementationContext Context(int? contextWindow, int? output) =>
        new(
            "agent",
            new NoopChatClient(),
            new ChatOptions(),
            null,
            new ToolEffectRegistry(),
            [],
            contextWindow,
            output
        );

    private sealed class NoopChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
