using FluentAssertions;

namespace Tandem.Tests.Composition;

public sealed class DeliveryPolicyRegressionTests
{
    [Fact]
    public void PlannerAndReviewer_AreReadOnlyRegardlessOfPipelineAuthority()
    {
        var state = CreateState() with { MutationAuthorized = true };

        ExecutorPolicies.AllowsWorkspaceMutation(BlockIds.Planner, state).Should().BeFalse();
        ExecutorPolicies.AllowsWorkspaceMutation(BlockIds.Reviewer, state).Should().BeFalse();
    }

    [Fact]
    public void Executor_WorkspaceMutationTracksEstablishedAuthority()
    {
        var state = CreateState();

        ExecutorPolicies.AllowsWorkspaceMutation(BlockIds.Executor, state).Should().BeFalse();
        ExecutorPolicies
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
        ExecutorPolicies.IsWorkspaceMutationTool(toolName).Should().BeTrue();
    }

    [Theory]
    [InlineData("file_access_read")]
    [InlineData("file_access_search")]
    [InlineData("file_access_list")]
    public void WorkspaceReadTools_AreNotTreatedAsMutation(string toolName)
    {
        ExecutorPolicies.IsWorkspaceMutationTool(toolName).Should().BeFalse();
    }

    [Fact]
    public void CheckpointPolicy_IsOwnedOnlyByExecutor()
    {
        ExecutorPolicies.OwnsCheckpoint(BlockIds.Executor).Should().BeTrue();
        ExecutorPolicies.OwnsCheckpoint(BlockIds.Planner).Should().BeFalse();
        ExecutorPolicies.OwnsCheckpoint(BlockIds.Reviewer).Should().BeFalse();
    }

    private static DeliveryState CreateState() =>
        DeliveryState.Create(new Packet("test", "/tmp/repo", "main", [], [], [], ""), "", "/tmp");
}
