using Tandem.Examples.Hosting;

namespace Tandem.Sample.CodeWriter;

public static class Program
{
    private static readonly string[] _requirements =
    [
        "Implement synchronous pure JavaScript slugify(input).",
        "Trim whitespace and lowercase the input.",
        "Remove Unicode diacritics.",
        "Replace runs of non-alphanumeric characters with one hyphen.",
        "Trim edge hyphens and never return repeated hyphens.",
        "Return an empty string when no alphanumeric characters remain.",
    ];

    public static Task<int> Main() =>
        ExampleHost.RunAsync(clients =>
        {
            Console.WriteLine("Node.js is required for JavaScript verification.");
            var capability = AgentCapabilities.Create<CodeWriterState, SubmitImplementation>(
                new SubmitImplementationCapability(),
                (state, submission) => state.RecordImplementation(submission)
            );
            var participants = CodeWriterDefinitions.Create(
                new CodeWriterClients(clients.DeepSeek, clients.Sol),
                capability
            );
            var pipeline = new CodeWriterComposition(participants).Build();
            return new ExampleRun<CodeWriterState>(
                pipeline,
                new CodeWriterState(_requirements),
                result => $"Implementation:\n{result.State.Implementation?.Source}",
                "code-writer-ledger.sqlite3"
            );
        });
}
