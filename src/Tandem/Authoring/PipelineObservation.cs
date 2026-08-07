namespace Tandem;

public interface IBlockExecutionObserver
{
    public ValueTask StartedAsync(string blockId, CancellationToken cancellationToken);

    public ValueTask CompletedAsync<TInput, TOutput>(
        string blockId,
        TInput input,
        TOutput output,
        TimeSpan duration,
        CancellationToken cancellationToken
    );
}

public interface ICommandOutputObserver
{
    public ValueTask CommandOutputAsync(
        string blockId,
        string command,
        string output,
        int exitCode,
        CancellationToken cancellationToken
    );
}
