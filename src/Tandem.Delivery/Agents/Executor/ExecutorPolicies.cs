using Tandem.Advanced;

namespace Tandem.Delivery;

public static class ExecutorPolicies
{
    public static AgentConversationDecision RetainUntilAcceptedReport(
        AgentMessageContext<DeliveryState> context,
        AgentMessageOutcome _
    ) =>
        context.State.ExecutorTransition is ExecutorTransition.ReportSubmitted
            ? new(AgentConversationRetention.Discard)
            : new(AgentConversationRetention.Retain);

    public static AgentTurnPolicy<DeliveryState> CreateTurnPolicy() =>
        new(
            maxContinuationAttempts: 2,
            (observation, _) =>
                ValueTask.FromResult<AgentTurnDirective?>(
                    !observation.Context.State.MutationAuthorized
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
}
