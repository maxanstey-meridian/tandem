namespace Tandem.Sample.CodeWriter;

public sealed record CodeWriterParticipants(
    AgentDefinition<CodeWriterState> Implementer,
    VerificationStage Verification,
    AgentDefinition<CodeWriterState> Reviewer,
    IPipelineNode<CodeWriterState> Complete,
    IPipelineNode<CodeWriterState> Failed
);
