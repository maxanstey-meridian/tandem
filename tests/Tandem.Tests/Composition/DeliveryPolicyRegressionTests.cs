using FluentAssertions;
using Tandem.Domain;

namespace Tandem.Tests.Composition;

public sealed class DeliveryPolicyRegressionTests
{
    [Fact]
    public void PlannerAndReviewer_AreReadOnlyRegardlessOfPipelineAuthority()
    {
        var state = CreateState() with { MutationAuthorized = true };

        DeliveryPolicies.AllowsWorkspaceMutation(BlockIds.Planner, state).Should().BeFalse();
        DeliveryPolicies.AllowsWorkspaceMutation(BlockIds.Reviewer, state).Should().BeFalse();
        DeliveryPolicies.LifecycleToolsFor(BlockIds.Planner).Should().BeEmpty();
        DeliveryPolicies.LifecycleToolsFor(BlockIds.Reviewer).Should().BeEmpty();
    }

    [Fact]
    public void Executor_ExposesItsCompleteLifecycleMutationSurface()
    {
        DeliveryPolicies
            .LifecycleToolsFor(BlockIds.Executor)
            .Should()
            .Equal("ask_planner", "submit_report");
    }

    [Fact]
    public void Executor_WorkspaceMutationTracksEstablishedAuthority()
    {
        var state = CreateState();

        DeliveryPolicies.AllowsWorkspaceMutation(BlockIds.Executor, state).Should().BeFalse();
        DeliveryPolicies
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
        DeliveryPolicies.IsWorkspaceMutationTool(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("file_access_read")]
    [InlineData("file_access_search")]
    [InlineData("file_access_list")]
    public void WorkspaceReadTools_AreNotTreatedAsMutation(string toolName)
    {
        DeliveryPolicies.IsWorkspaceMutationTool(toolName).Should().BeFalse();
    }

    [Fact]
    public void CheckpointPolicy_IsOwnedOnlyByExecutor()
    {
        DeliveryPolicies.OwnsCheckpointPolicy(BlockIds.Executor).Should().BeTrue();
        DeliveryPolicies.OwnsCheckpointPolicy(BlockIds.Planner).Should().BeFalse();
        DeliveryPolicies.OwnsCheckpointPolicy(BlockIds.Reviewer).Should().BeFalse();
    }

    private static DeliveryState CreateState() =>
        DeliveryState.Create(new Packet("test", "/tmp/repo", "main", [], [], [], ""), "", "/tmp");
}
