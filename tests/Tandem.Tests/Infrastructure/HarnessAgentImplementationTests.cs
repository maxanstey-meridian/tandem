using FluentAssertions;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

#pragma warning disable MAAI001

namespace Tandem.Tests.Infrastructure;

public sealed class HarnessAgentImplementationTests
{
    [Fact]
    public void Every_workspace_tool_is_registered_once_with_its_effect()
    {
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => "", "file_access_grep")],
        };
        var effects = new ToolEffectRegistry();
        var resolved = new ResolvedAgentWorkspace(
            Path.GetTempPath(),
            new HashSet<WorkspaceToolKind> { WorkspaceToolKind.ReadFile, WorkspaceToolKind.Grep },
            false,
            false,
            false,
            false,
            []
        );

        FluentActions
            .Invoking(() => HarnessTools.AddFileTools(options, effects, resolved))
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Agent already exposes tool 'file_access_grep'.");
        effects.TryGet("file_access_read", out var read).Should().BeTrue();
        read.Effect.Should().Be(ToolEffect.Read);
        read.Evidence.Should().Be(ToolEvidence.RepositoryInspection);
    }

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
        options.MaximumIterationsPerRequest.Should().Be(40);
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
    public void Explicit_setting_disables_compaction_without_discarding_context_limits()
    {
        var options = HarnessAgentImplementation.CreateOptions(
            Context(330_000, 32_000, disableCompaction: true),
            "Harness."
        );

        options.MaxContextWindowTokens.Should().Be(330_000);
        options.MaxOutputTokens.Should().Be(32_000);
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

        var pipeline = options
            .CompactionStrategy.Should()
            .BeOfType<PipelineCompactionStrategy>()
            .Subject;
        var strategies = pipeline.Strategies;
        var toolResult = strategies
            .Should()
            .ContainSingle(s => s is ToolResultCompactionStrategy)
            .Which.Should()
            .BeOfType<ToolResultCompactionStrategy>()
            .Subject;

        toolResult.ToolCallFormatter.Should().NotBeNull();
        toolResult.MinimumPreservedGroups.Should().Be(10);
        strategies
            .Should()
            .ContainSingle(s => s is TruncationCompactionStrategy)
            .Which.Should()
            .BeOfType<TruncationCompactionStrategy>()
            .Subject.MinimumPreservedGroups.Should()
            .Be(10);
    }

    private static AgentImplementationContext Context(
        int? contextWindow,
        int? output,
        bool disableCompaction = false
    ) =>
        new(
            "agent",
            new TestChatClient(),
            new ChatOptions(),
            null,
            new ToolEffectRegistry(),
            [],
            contextWindow,
            output,
            disableCompaction
        );
}
