using System.Text.Json;
using Tandem;
using Tandem.Examples.Hosting;

namespace Examples.Debate;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        ExampleHost.RunAsync(clients =>
        {
            var verdict = AgentCapabilities.Create<DebateState, SubmitVerdict>(
                new SubmitVerdictCapability(),
                (state, request) => state.RecordVerdict(request)
            );
            var participants = DebateDefinitions.Create(
                new DebateOptions(clients.DeepSeek, clients.Sol, clients.Sol),
                verdict
            );
            var pipeline = new DebateComposition(participants).Build();
            var question =
                args.Length == 0
                    ? "Should typed composition own lifecycle state?"
                    : string.Join(' ', args);
            return new ExampleRun<DebateState>(
                pipeline,
                new DebateState(question, [], 0, null),
                result => $"Verdict: {JsonSerializer.Serialize(result.State.Verdict)}"
            );
        });
}
