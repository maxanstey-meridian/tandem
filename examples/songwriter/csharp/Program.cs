using Tandem.Examples.Hosting;

namespace Examples.Songwriter;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        ExampleHost.RunAsync(clients =>
        {
            var participants = SongwriterDefinitions.Create(
                new SongwriterClients(clients.DeepSeek, clients.Sol)
            );
            var pipeline = new SongwriterComposition(participants).Build();
            var brief =
                args.Length == 0
                    ? "An optimistic song about rebuilding after a storm."
                    : string.Join(' ', args);
            return new ExampleRun<SongwriterState>(
                pipeline,
                new SongwriterState(brief),
                result => $"Lyrics:\n{result.State.Lyrics}"
            );
        });
}
