namespace Tandem.Sample.CodeWriter;

public sealed record CodeWriterState(
    IReadOnlyList<string> Requirements,
    ImplementationCandidate? Implementation = null,
    VerificationResult? Verification = null,
    ReviewDecision? Review = null
)
{
    public CodeWriterState RecordImplementation(SubmitImplementation submission) =>
        this with
        {
            Implementation = new(submission.Implementation, submission.Rationale),
            Verification = null,
            Review = null,
        };

    public CodeWriterState RecordVerification(VerificationResult verification) =>
        this with
        {
            Verification = verification,
        };

    public CodeWriterState RecordReview(ReviewDecision review) => this with { Review = review };
}

public sealed record ImplementationCandidate(string Source, string Rationale);

public sealed record VerificationCase(
    string Input,
    string Expected,
    string? Actual,
    bool Passed,
    string? Error
);

public sealed record VerificationResult(
    bool Passed,
    IReadOnlyList<VerificationCase> Cases,
    string? Error
);

public enum ReviewDisposition
{
    Accept,
    RequestChanges,
}

public sealed record ReviewDecision(
    ReviewDisposition Decision,
    string Summary,
    IReadOnlyList<string> Findings
);

public sealed record SubmitImplementation(string Implementation, string Rationale);
