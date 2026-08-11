namespace Tandem.Tests;

internal sealed class TestCapabilityDefinition<TState, TRequest>(
    string toolName,
    string instructions,
    FluentValidation.IValidator<TRequest> validator,
    Func<TRequest, string> summarize
) : IAgentCapabilityDefinition<TState, TRequest>
    where TRequest : class
{
    public string ToolName => toolName;
    public string Instructions => instructions;
    public FluentValidation.IValidator<TRequest> Validator => validator;

    public string Summarize(TRequest request) => summarize(request);
}

internal sealed class TestOutputDefinition<TState, TOutput>(
    string instructions,
    FluentValidation.IValidator<TOutput> validator
) : IAgentOutputDefinition<TState, TOutput>
{
    public string Instructions => instructions;
    public FluentValidation.IValidator<TOutput> Validator => validator;
}

internal sealed class CompositePipelineObserver(params IPipelineObserver[] observers)
    : IPipelineObserver
{
    public async ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        foreach (var observer in observers)
        {
            await observer.ObserveAsync(observation, cancellationToken);
        }
    }
}
