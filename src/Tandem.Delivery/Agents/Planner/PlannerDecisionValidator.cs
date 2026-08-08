using FluentValidation;

namespace Tandem.Delivery;

public sealed class PlannerDecisionValidator : AbstractValidator<PlannerDecision>
{
    private static readonly HashSet<string> _placeholders = new(
        ["todo", "lgtm", "done", "looks good", "n/a"],
        StringComparer.OrdinalIgnoreCase
    );

    public PlannerDecisionValidator()
    {
        RuleFor(decision => decision.Rationale).Must(BeMeaningful);
        RuleFor(decision => decision.EvidenceUsed).NotEmpty();
        RuleForEach(decision => decision.EvidenceUsed).Must(BeMeaningful);
        RuleFor(decision => decision.Constraints)
            .Empty()
            .When(decision => decision.Decision == PlannerDecisionValue.Proceed);
        RuleFor(decision => decision.Constraints)
            .NotEmpty()
            .When(decision => decision.Decision == PlannerDecisionValue.ProceedWithConstraints);
        RuleForEach(decision => decision.Constraints).Must(BeMeaningful);
        RuleFor(decision => decision.HumanQuestion)
            .NotEmpty()
            .Must(question => !string.Equals(question, "N/A", StringComparison.OrdinalIgnoreCase))
            .When(decision => decision.Decision == PlannerDecisionValue.NeedsHuman);
        RuleFor(decision => decision.HumanQuestion)
            .Null()
            .When(decision => decision.Decision != PlannerDecisionValue.NeedsHuman);
    }

    internal static bool BeMeaningful(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !_placeholders.Contains(value.Trim());
}

public sealed class PlannerDecisionOutput : IAgentOutputDefinition<DeliveryState, PlannerDecision>
{
    public string Instructions =>
        "Return a validated planning decision grounded in the packet and repository evidence.";

    public IValidator<PlannerDecision> Validator { get; } = new PlannerDecisionValidator();

    public IReadOnlyList<AgentOutputExample<PlannerDecision>> Examples(DeliveryState state) =>
        [
            new(
                state.Packet.Title,
                new PlannerDecision(
                    PlannerDecisionValue.Proceed,
                    "The packet is actionable and repository evidence supports direct implementation.",
                    [],
                    ["README.md"],
                    null
                )
            ),
        ];
}
