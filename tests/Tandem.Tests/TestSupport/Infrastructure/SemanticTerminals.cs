namespace Tandem.Tests;

internal sealed class TestCompletion<TState>(string id) : IPipelineCompletion<TState>
{
    public string Id => id;

    public string Summarize(TState state) => id;
}

internal sealed class TestFailure<TState>(string id) : IPipelineFailure<TState>
{
    public string Id => id;

    public string Summarize(TState state) => id;
}
