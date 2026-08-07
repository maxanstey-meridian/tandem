namespace Tandem.Delivery;

public static class OutcomeKinds
{
    public const string WorkspacePrepared = "workspace.prepared";
    public const string PlannerRequested = "planner.requested";
    public const string ReportSubmitted = "report.submitted";
    public const string CheckpointWritten = "checkpoint.written";
    public const string PlannerProceed = "planner.proceed";
    public const string PlannerProceedWithConstraints = "planner.proceed_with_constraints";
    public const string PlannerNeedsHuman = "planner.needs_human";
    public const string PlannerStop = "planner.stop";
    public const string CandidateCaptured = "candidate.captured";
    public const string CommandPassed = "command.passed";
    public const string CommandFailed = "command.failed";
    public const string ReviewAccepted = "review.accepted";
    public const string ReviewChangesRequested = "review.changes_requested";
    public const string ReviewNeedsHuman = "review.needs_human";
    public const string RunReady = "run.ready";
    public const string RunFailed = "run.failed";
}

public static class BlockIds
{
    public const string Prepare = "prepare";
    public const string Executor = "executor";
    public const string Planner = "planner";
    public const string CaptureCandidate = "capture-candidate";
    public const string Verify = "verify";
    public const string Reviewer = "reviewer";
    public const string Complete = "complete";
    public const string Failed = "failed";
    public const string HumanQuestion = "HumanInput--request";
    public const string ApplyHumanAnswer = "HumanInput--resume";
}
