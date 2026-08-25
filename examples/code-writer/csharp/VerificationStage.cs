using Tandem;

namespace Examples.CodeWriter;

[PipelineStage("verification")]
public sealed partial class VerificationStage
{
    private readonly ImplementationAssessment _assessment = new();

    public async ValueTask<CodeWriterState> ExecuteAsync(
        CodeWriterState state,
        CancellationToken cancellationToken
    )
    {
        var source =
            state.Implementation?.Source
            ?? throw new InvalidOperationException("Verification requires an implementation.");
        var verification = await _assessment.AssessAsync(source, cancellationToken);
        return state.RecordVerification(verification);
    }
}
