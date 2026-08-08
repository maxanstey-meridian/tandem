namespace Tandem.Domain;

public sealed record HumanQuestion(string SourceBlockId, string Question, string Reason);

public sealed record HumanAnswer(string Text);
