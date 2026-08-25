using Tandem;

namespace Examples.CodeWriter;

public sealed class CodeWriterComposition(CodeWriterParticipants codeWriter)
{
    public Pipeline<CodeWriterState> Build() =>
        Pipeline
            .Start(
                at: codeWriter.Implementer,
                name: "code-writer",
                description: "Implement and verify a function until review accepts it."
            )
            .Route(
                on: codeWriter.Implementer.Success,
                to: codeWriter.Verification,
                label: "implementation submitted"
            )
            .Route(
                on: codeWriter.Implementer.Failed,
                to: codeWriter.Failed,
                label: "implementer failed"
            )
            .Route(
                from: codeWriter.Verification,
                when: state => state.Verification?.Passed is true,
                to: codeWriter.Reviewer,
                label: "verification passed"
            )
            .Route(
                from: codeWriter.Verification,
                when: state => state.Verification?.Passed is false,
                to: codeWriter.Implementer,
                label: "verification failed"
            )
            .Route(
                on: codeWriter.Reviewer.Success,
                when: state => state.Review?.Decision == ReviewDisposition.RequestChanges,
                to: codeWriter.Implementer,
                label: "changes requested"
            )
            .Route(
                on: codeWriter.Reviewer.Success,
                when: state => state.Review?.Decision == ReviewDisposition.Accept,
                to: codeWriter.Complete,
                label: "accepted"
            )
            .Route(on: codeWriter.Reviewer.Failed, to: codeWriter.Failed, label: "reviewer failed")
            .Persist()
            .Build(codeWriter.Complete, codeWriter.Failed);
}
