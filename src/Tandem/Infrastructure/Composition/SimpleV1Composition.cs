using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem.Infrastructure.Composition;

public sealed class SimpleV1Composition
{
    private readonly string _tandemHome;
    private readonly string? _tandemExePath;
    private readonly Func<string, Microsoft.Extensions.AI.IChatClient> _chatClientFactory;
    private readonly Func<string, ResolvedProfile> _profileResolver;

    public SimpleV1Composition(
        string tandemHome,
        Func<string, Microsoft.Extensions.AI.IChatClient> chatClientFactory,
        Func<string, ResolvedProfile> profileResolver,
        string? tandemExePath = null
    )
    {
        _tandemHome = tandemHome;
        _tandemExePath = tandemExePath;
        _chatClientFactory = chatClientFactory;
        _profileResolver = profileResolver;
    }

    public Workflow Build(Action<AgentResponseUpdate>? onUpdate = null)
    {
        var prepare = new PrepareWorkspaceBlock();
        var executor = CreateAgentBlock(
            BlockIds.Executor,
            "implementation",
            ExecutorInstructions,
            WorkspaceAccess.MutationGated,
            ["ask_planner", "submit_report", "write_checkpoint"],
            onUpdate
        );
        var planner = CreateAgentBlock(
            BlockIds.Planner,
            "planning",
            PlannerInstructions,
            WorkspaceAccess.ReadOnly,
            [],
            onUpdate,
            ParsePlannerDecision
        );
        var captureCandidate = new CaptureCandidateBlock();
        var verify = new VerificationBlock();
        var reviewer = CreateAgentBlock(
            BlockIds.Reviewer,
            "review",
            ReviewerInstructions,
            WorkspaceAccess.ReadOnly,
            [],
            onUpdate,
            ParseReviewDecision
        );
        var complete = new CompleteBlock();
        var waiting = new WaitingBlock();
        var failed = new FailedBlock();

        var prepareBinding = prepare.BindExecutor();
        var executorBinding = executor.BindExecutor();
        var plannerBinding = planner.BindExecutor();
        var captureBinding = captureCandidate.BindExecutor();
        var verifyBinding = verify.BindExecutor();
        var reviewerBinding = reviewer.BindExecutor();
        var completeBinding = complete.BindExecutor();
        var waitingBinding = waiting.BindExecutor();
        var failedBinding = failed.BindExecutor();

        var builder = new WorkflowBuilder(prepareBinding).WithName("simple-v1");

        builder = AddOutcomeEdge(
            builder,
            prepareBinding,
            executorBinding,
            OutcomeKinds.WorkspacePrepared
        );
        builder = builder.AddEdge(prepareBinding, failedBinding, CatchAllPrepared());

        builder = AddOutcomeEdge(
            builder,
            executorBinding,
            plannerBinding,
            OutcomeKinds.PlannerRequested
        );
        builder = AddOutcomeEdge(
            builder,
            executorBinding,
            captureBinding,
            OutcomeKinds.ReportSubmitted
        );
        builder = AddOutcomeEdge(
            builder,
            executorBinding,
            executorBinding,
            OutcomeKinds.CheckpointWritten
        );
        builder = builder.AddEdge(executorBinding, failedBinding, CatchAllExecutor());

        builder = AddOutcomeEdge(
            builder,
            plannerBinding,
            executorBinding,
            OutcomeKinds.PlannerProceed
        );
        builder = AddOutcomeEdge(
            builder,
            plannerBinding,
            executorBinding,
            OutcomeKinds.PlannerProceedWithConstraints
        );
        builder = AddOutcomeEdge(
            builder,
            plannerBinding,
            waitingBinding,
            OutcomeKinds.PlannerNeedsHuman
        );
        builder = AddOutcomeEdge(builder, plannerBinding, failedBinding, OutcomeKinds.PlannerStop);
        builder = builder.AddEdge(plannerBinding, failedBinding, CatchAllPlanner());

        builder = AddOutcomeEdge(
            builder,
            captureBinding,
            verifyBinding,
            OutcomeKinds.CandidateCaptured,
            HasVerificationCommands
        );
        builder = AddOutcomeEdge(
            builder,
            captureBinding,
            reviewerBinding,
            OutcomeKinds.CandidateCaptured,
            NoVerificationCommands
        );
        builder = builder.AddEdge(captureBinding, failedBinding, CatchAllCandidate());

        builder = AddOutcomeEdge(
            builder,
            verifyBinding,
            verifyBinding,
            OutcomeKinds.CommandPassed,
            HasRemainingCommands
        );
        builder = AddOutcomeEdge(
            builder,
            verifyBinding,
            reviewerBinding,
            OutcomeKinds.CommandPassed,
            AllCommandsComplete
        );
        builder = AddOutcomeEdge(
            builder,
            verifyBinding,
            executorBinding,
            OutcomeKinds.CommandFailed
        );
        builder = builder.AddEdge(verifyBinding, failedBinding, CatchAllVerify());

        builder = AddOutcomeEdge(
            builder,
            reviewerBinding,
            completeBinding,
            OutcomeKinds.ReviewAccepted
        );
        builder = AddOutcomeEdge(
            builder,
            reviewerBinding,
            executorBinding,
            OutcomeKinds.ReviewChangesRequested
        );
        builder = AddOutcomeEdge(
            builder,
            reviewerBinding,
            waitingBinding,
            OutcomeKinds.ReviewNeedsHuman
        );
        builder = builder.AddEdge(reviewerBinding, failedBinding, CatchAllReviewer());

        builder = builder.WithOutputFrom(completeBinding, waitingBinding, failedBinding);
        return builder.Build();
    }

    private AgentBlock CreateAgentBlock(
        string blockId,
        string profileName,
        string instructions,
        WorkspaceAccess access,
        IReadOnlyList<string> lifecycleTools,
        Action<AgentResponseUpdate>? onUpdate,
        StructuredOutputParser? structuredOutput = null
    )
    {
        var profile = _profileResolver(profileName);
        var checkpoint = new CheckpointPolicy(
            profile.ContextWindowTokens,
            profile.MaxOutputTokens,
            profile.CheckpointAtPercent
        );

        var config = new AgentBlockConfig(
            blockId,
            profileName,
            instructions,
            access,
            lifecycleTools,
            structuredOutput,
            checkpoint
        );
        return new AgentBlock(
            config,
            _chatClientFactory(profileName),
            _tandemHome,
            _tandemExePath,
            onUpdate
        );
    }

    private static StructuredOutcome ParsePlannerDecision(string assistantText, PipelineContext ctx)
    {
        var json = ExtractJson(assistantText);
        var decision =
            JsonSerializer.Deserialize<PlannerDecision>(json, _plannerJsonOptions)
            ?? throw new InvalidOperationException(
                "Failed to parse PlannerDecision from model response."
            );
        var kind = decision.Decision switch
        {
            PlannerDecisionValue.Proceed => OutcomeKinds.PlannerProceed,
            PlannerDecisionValue.ProceedWithConstraints =>
                OutcomeKinds.PlannerProceedWithConstraints,
            PlannerDecisionValue.NeedsHuman => OutcomeKinds.PlannerNeedsHuman,
            PlannerDecisionValue.Stop => OutcomeKinds.PlannerStop,
            _ => throw new InvalidOperationException(
                $"Unknown planner decision: {decision.Decision}"
            ),
        };
        var payload = JsonSerializer.SerializeToElement(decision, _plannerJsonOptions);
        var updatedCtx = ctx with
        {
            PlannerDecision = decision,
            PlannerConstraints =
                decision.Constraints.Count > 0 ? decision.Constraints : ctx.PlannerConstraints,
        };
        return new StructuredOutcome(kind, decision.Rationale, payload, updatedCtx);
    }

    private static StructuredOutcome ParseReviewDecision(string assistantText, PipelineContext ctx)
    {
        var json = ExtractJson(assistantText);
        var decision =
            JsonSerializer.Deserialize<ReviewDecision>(json, _reviewJsonOptions)
            ?? throw new InvalidOperationException(
                "Failed to parse ReviewDecision from model response."
            );
        var kind = decision.Decision switch
        {
            ReviewDecisionValue.Accept => OutcomeKinds.ReviewAccepted,
            ReviewDecisionValue.RequestChanges => OutcomeKinds.ReviewChangesRequested,
            ReviewDecisionValue.NeedsHuman => OutcomeKinds.ReviewNeedsHuman,
            _ => throw new InvalidOperationException(
                $"Unknown review decision: {decision.Decision}"
            ),
        };
        var payload = JsonSerializer.SerializeToElement(decision, _reviewJsonOptions);
        return new StructuredOutcome(kind, decision.Summary, payload);
    }

    private static string ExtractJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            throw new InvalidOperationException("Model response contains no JSON object.");
        }

        var depth = 0;
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text.Substring(start, i - start + 1);
                }
            }
        }

        throw new InvalidOperationException("Model response contains incomplete JSON object.");
    }

    private static readonly JsonSerializerOptions _plannerJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly JsonSerializerOptions _reviewJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static WorkflowBuilder AddOutcomeEdge(
        WorkflowBuilder builder,
        ExecutorBinding source,
        ExecutorBinding target,
        string outcomeKind,
        Func<PipelineMessage, bool>? extraCondition = null
    )
    {
        return builder.AddEdge<PipelineMessage>(
            source,
            target,
            msg => msg!.LatestOutcome?.Kind == outcomeKind && (extraCondition?.Invoke(msg) ?? true)
        );
    }

    private static Func<PipelineMessage?, bool> CatchAllPrepared() =>
        msg => msg!.LatestOutcome?.Kind != OutcomeKinds.WorkspacePrepared;

    private static Func<PipelineMessage?, bool> CatchAllExecutor() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerRequested
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReportSubmitted
            && msg!.LatestOutcome?.Kind != OutcomeKinds.CheckpointWritten;

    private static Func<PipelineMessage?, bool> CatchAllPlanner() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerProceed
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerProceedWithConstraints
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerNeedsHuman
            && msg!.LatestOutcome?.Kind != OutcomeKinds.PlannerStop;

    private static Func<PipelineMessage?, bool> CatchAllCandidate() =>
        msg => msg!.LatestOutcome?.Kind != OutcomeKinds.CandidateCaptured;

    private static Func<PipelineMessage?, bool> CatchAllVerify() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.CommandPassed
            && msg!.LatestOutcome?.Kind != OutcomeKinds.CommandFailed;

    private static Func<PipelineMessage?, bool> CatchAllReviewer() =>
        msg =>
            msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewAccepted
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewChangesRequested
            && msg!.LatestOutcome?.Kind != OutcomeKinds.ReviewNeedsHuman;

    private static bool HasVerificationCommands(PipelineMessage msg) =>
        msg.Context.Packet.Verification.Count > 0;

    private static bool NoVerificationCommands(PipelineMessage msg) =>
        msg.Context.Packet.Verification.Count == 0;

    private static bool HasRemainingCommands(PipelineMessage msg) =>
        msg.Context.VerificationIndex < msg.Context.Packet.Verification.Count;

    private static bool AllCommandsComplete(PipelineMessage msg) =>
        msg.Context.VerificationIndex >= msg.Context.Packet.Verification.Count;

    private const string ExecutorInstructions = """
        You are Tandem's implementation block.

        Inspect the workspace and work toward the packet outcomes. When mutation
        authority is closed, use read-only tools to understand the repository and call
        ask_planner with your proposed approach before editing. When authority is open,
        implement the approved approach and constraints.

        Call ask_planner whenever independent guidance is required. During a
        checkpoint-only invocation, call write_checkpoint with the supplied work state.
        When the implementation is ready for verification, call submit_report with
        outcome claims and repository evidence.

        An accepted lifecycle call ends the current turn. Do not represent planner,
        verification, or reviewer decisions yourself.
        """;

    private const string PlannerInstructions = """
        You are Tandem's planner block.

        Review the packet outcomes and constraints, the executor's question,
        proposed approach, and evidence. Read files as needed. Return a structured
        decision: Proceed, ProceedWithConstraints, NeedsHuman, or Stop. Include a
        rationale, any new constraints, and the evidence you used.
        """;

    private const string ReviewerInstructions = """
        You are Tandem's reviewer block.

        Evaluate the candidate diff against the packet outcomes, planner constraints,
        implementation report, and verification results. You may inspect changed files
        through your read-only tools. Return a structured decision: Accept,
        RequestChanges, or NeedsHuman. Include findings with severity, description,
        and evidence for each issue found.
        """;
}
