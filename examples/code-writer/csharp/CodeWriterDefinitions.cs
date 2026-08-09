using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Tandem.Sample.CodeWriter;

public sealed record CodeWriterClients(IChatClient Implementer, IChatClient Reviewer);

public static class CodeWriterDefinitions
{
    public static CodeWriterParticipants Create(
        CodeWriterClients clients,
        AgentCapability<CodeWriterState> submitImplementation
    ) =>
        new(
            Agent
                .Create<CodeWriterState>(
                    "implementer",
                    "Implement the requested function. Submit the actual, complete JavaScript function expression and a concise rationale through submit_implementation. The source must be exactly one synchronous function expression accepting one input and returning a string.",
                    clients.Implementer
                )
                .WithMessage(ImplementerMessage)
                .WithCapability(submitImplementation)
                .ContinueSession()
                .Build(),
            new VerificationStage(),
            Agent
                .Create<CodeWriterState>(
                    "reviewer",
                    "Review the exact implementation against the requirements and passing verification evidence. Return Accept or RequestChanges with a concise summary. RequestChanges must include concrete findings.",
                    clients.Reviewer
                )
                .WithMessage(ReviewerMessage)
                .WithOutput(
                    new ReviewDecisionOutput(),
                    (state, review) => state.RecordReview(review)
                )
                .Build(),
            PipelineNodes.Complete(new CodeWriterComplete()),
            PipelineNodes.Failed(new CodeWriterFailed())
        );

    private static string ImplementerMessage(CodeWriterState state) =>
        $"Requirements: {JsonSerializer.Serialize(state.Requirements)}\n"
        + (
            state.Implementation is null
                ? "No implementation has been submitted."
                : $"Current implementation: {JsonSerializer.Serialize(state.Implementation)}"
        )
        + "\n"
        + (
            state.Verification is null
                ? "No verification feedback is pending."
                : $"Verification feedback to address: {JsonSerializer.Serialize(state.Verification)}"
        )
        + "\n"
        + (
            state.Review is null
                ? "No reviewer feedback is pending."
                : $"Reviewer feedback to address: {JsonSerializer.Serialize(state.Review)}"
        );

    private static string ReviewerMessage(CodeWriterState state) =>
        $"Requirements: {JsonSerializer.Serialize(state.Requirements)}\n"
        + $"Exact source: {state.Implementation!.Source}\n"
        + $"Passing verification evidence: {JsonSerializer.Serialize(state.Verification)}";
}

public sealed class CodeWriterComplete : IPipelineCompletion<CodeWriterState>
{
    public string Id => "done";

    public string Summarize(CodeWriterState state) => state.Review!.Summary;
}

public sealed class CodeWriterFailed : IPipelineFailure<CodeWriterState>
{
    public string Id => "failed";

    public string Summarize(CodeWriterState state) =>
        "An agent failed before the code could be accepted.";
}
