using Tandem.Domain;

namespace Tandem.Delivery;

public static class DeliveryPolicies
{
    public static StructuredOutputAcceptancePolicy<DeliveryState> CreatePlannerGroundingPolicy() =>
        StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
            result =>
                result.Outcome?.Kind
                    is OutcomeKinds.PlannerProceed
                        or OutcomeKinds.PlannerProceedWithConstraints
                || result.Candidate
                    is PlannerDecision
                    {
                        Decision: PlannerDecisionValue.Proceed
                            or PlannerDecisionValue.ProceedWithConstraints,
                    },
            IsRepositoryInspectionTool,
            correction: "Accepted planner decisions require repository inspection in this consult. "
                + "Use an available read-only repository tool to verify the material files and seams, "
                + "then return only the corrected JSON decision with concrete evidenceUsed entries."
        );

    public static StructuredOutputAcceptancePolicy<DeliveryState> CreateReviewerGroundingPolicy() =>
        StructuredOutputAcceptancePolicies.RequireToolCallWhen<DeliveryState>(
            result =>
                result.Outcome?.Kind
                    is OutcomeKinds.ReviewAccepted
                        or OutcomeKinds.ReviewChangesRequested
                || result.Candidate
                    is ReviewDecision
                    {
                        Decision: ReviewDecisionValue.Accept or ReviewDecisionValue.RequestChanges,
                    },
            IsRepositoryInspectionTool,
            correction: "Accept and RequestChanges require repository inspection in this review. "
                + "Use an available read-only repository tool to verify the candidate and packet outcomes, "
                + "then return only the corrected JSON decision with concrete outcome evidence."
        );

    public static ToolInterceptor<DeliveryState> CreateMutationGate() =>
        (message, invocation, ct) =>
        {
            if (message.State.MutationAuthorized || !IsWorkspaceMutationTool(invocation.Name))
            {
                return ValueTask.FromResult<ToolInterceptionResult?>(null);
            }

            return ValueTask.FromResult<ToolInterceptionResult?>(
                new ToolInterceptionResult.Blocked(
                    """
                    MUTATION GATE CLOSED: Your edit was NOT applied — no file was changed.
                    Mutation authority is not yet granted. Call ask_planner with your
                    proposed approach and evidence. Reads remain available for gathering
                    evidence. Continue only on proceed or proceed_with_constraints.
                    """
                )
            );
        };

    public static bool IsWorkspaceMutationTool(string name) =>
        name.StartsWith("file_access_write", StringComparison.Ordinal)
        || name.StartsWith("file_access_replace", StringComparison.Ordinal)
        || name.StartsWith("file_access_delete", StringComparison.Ordinal)
        || name.StartsWith("file_access_move", StringComparison.Ordinal)
        || name.StartsWith("file_access_create", StringComparison.Ordinal);

    public static bool OwnsCheckpointPolicy(string blockId) => blockId == BlockIds.Executor;

    public static bool AllowsWorkspaceMutation(string blockId, DeliveryState state) =>
        blockId == BlockIds.Executor && state.MutationAuthorized;

    public static AgentTurnPolicy<DeliveryState> CreateExecutorTurnPolicy() =>
        new(
            maxContinuationAttempts: 2,
            (observation, _) =>
                ValueTask.FromResult<AgentTurnDirective?>(
                    !observation.Message.State.MutationAuthorized
                        ? new AgentTurnDirective(
                            """
                            Your previous response was not a lifecycle route. Continue the
                            executor turn by calling ask_planner now with the question you
                            need answered, your proposed approach, and repository evidence.
                            Do not answer with prose; the next action must be the ask_planner
                            tool call.
                            """,
                            RequiredToolName: "ask_planner"
                        )
                        : new AgentTurnDirective(
                            """
                            Your previous response was not a lifecycle route. Continue the
                            implementation and call submit_report when the packet outcomes are
                            ready for verification. Do not treat prose as completion.
                            """,
                            RequiredToolName: "submit_report"
                        )
                )
        );

    public static MessageAugmentation<DeliveryState> CreateDiffAugmentation(
        DeliveryDiffAcquisition diffAcquisition
    ) =>
        async (message, ct) =>
        {
            var state = message.State;
            return state.CandidateSha is null || string.IsNullOrEmpty(state.PinnedBaseSha)
                ? null
                : await diffAcquisition.AcquireAsync(state, ct);
        };

    private static bool IsRepositoryInspectionTool(string name) =>
        name is "read" or "grep" or "glob"
        || name.StartsWith("file_access_read", StringComparison.Ordinal)
        || name.StartsWith("file_access_search", StringComparison.Ordinal)
        || name.StartsWith("file_access_list", StringComparison.Ordinal)
        || name.StartsWith("gitnexus_", StringComparison.Ordinal)
        || name.Contains("ast_grep", StringComparison.Ordinal);
}
