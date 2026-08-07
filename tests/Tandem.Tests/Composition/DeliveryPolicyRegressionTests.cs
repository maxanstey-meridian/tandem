using FluentAssertions;
using Tandem.Domain;
using Tandem.Infrastructure.Composition;

namespace Tandem.Tests.Composition;

public sealed class DeliveryPolicyRegressionTests
{
    [Fact]
    public void PlannerAndReviewer_AreReadOnlyRegardlessOfPipelineAuthority()
    {
        var state = CreateState() with { MutationAuthorized = true };

        DeliveryComposition.AllowsWorkspaceMutation(BlockIds.Planner, state).Should().BeFalse();
        DeliveryComposition.AllowsWorkspaceMutation(BlockIds.Reviewer, state).Should().BeFalse();
        DeliveryComposition.LifecycleToolsFor(BlockIds.Planner).Should().BeEmpty();
        DeliveryComposition.LifecycleToolsFor(BlockIds.Reviewer).Should().BeEmpty();
    }

    [Fact]
    public void Executor_ExposesItsCompleteLifecycleMutationSurface()
    {
        DeliveryComposition
            .LifecycleToolsFor(BlockIds.Executor)
            .Should()
            .Equal("ask_planner", "submit_report");
    }

    [Fact]
    public void Executor_WorkspaceMutationTracksEstablishedAuthority()
    {
        var state = CreateState();

        DeliveryComposition.AllowsWorkspaceMutation(BlockIds.Executor, state).Should().BeFalse();
        DeliveryComposition
            .AllowsWorkspaceMutation(BlockIds.Executor, state with { MutationAuthorized = true })
            .Should()
            .BeTrue();
    }

    [Theory]
    [InlineData("file_access_write")]
    [InlineData("file_access_replace")]
    [InlineData("file_access_delete")]
    [InlineData("file_access_move")]
    [InlineData("file_access_create")]
    public void ExecutorMutationGate_CoversEveryRegisteredWorkspaceMutationTool(string toolName)
    {
        DeliveryComposition.IsWorkspaceMutationTool(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("file_access_read")]
    [InlineData("file_access_search")]
    [InlineData("file_access_list")]
    public void WorkspaceReadTools_AreNotTreatedAsMutation(string toolName)
    {
        DeliveryComposition.IsWorkspaceMutationTool(toolName).Should().BeFalse();
    }

    [Fact]
    public void CheckpointPolicy_IsOwnedOnlyByExecutor()
    {
        DeliveryComposition.OwnsCheckpointPolicy(BlockIds.Executor).Should().BeTrue();
        DeliveryComposition.OwnsCheckpointPolicy(BlockIds.Planner).Should().BeFalse();
        DeliveryComposition.OwnsCheckpointPolicy(BlockIds.Reviewer).Should().BeFalse();
    }

    private static DeliveryState CreateState() =>
        DeliveryState.Create(new Packet("test", "/tmp/repo", "main", [], [], [], ""), "", "/tmp");
}
