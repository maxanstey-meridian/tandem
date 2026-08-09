using Tandem.Examples.Hosting;

namespace Tandem.Sample.Songwriter;

public static class Program
{
    public static Task<int> Main(string[] args) =>
        ExampleHost.RunAsync(
            async (clients, cancellationToken) =>
            {
                var participants = SongwriterDefinitions.Create(
                    new SongwriterClients(clients.DeepSeek, clients.Sol)
                );
                var pipeline = new SongwriterComposition(participants).Build();
                var brief =
                    args.Length == 0
                        ? "An optimistic song about rebuilding after a storm."
                        : string.Join(' ', args);
                var result = await new PipelineRunner().RunAsync(
                    pipeline,
                    new SongwriterState(brief),
                    cancellationToken: cancellationToken
                );
                return ExampleHost.PrintResult(result, $"Lyrics:\n{result.State.Lyrics}");
            }
        );
}
