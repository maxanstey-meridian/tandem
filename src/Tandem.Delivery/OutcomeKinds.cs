namespace Tandem.Delivery;

public static class OutcomeKinds
{
    public const string PlannerRequested = "planner.requested";
    public const string ReportSubmitted = "report.submitted";
    public const string CheckpointWritten = "checkpoint.written";
    public const string CommandPassed = "command.passed";
    public const string CommandFailed = "command.failed";
    public const string RunReady = "run.ready";
    public const string RunFailed = "run.failed";
}

public static class DeliveryIds
{
    public const string Prepare = "prepare";
    public const string Executor = "executor";
    public const string Planner = "planner";
    public const string CaptureCandidate = "capture-candidate";
    public const string Verify = "verify";
    public const string Reviewer = "reviewer";
    public const string Complete = "complete";
    public const string Failed = "failed";
}
