using FluentValidation;

namespace Tandem;

public interface IAgentOutputDefinition<TState, TOutput>
{
    public string Instructions { get; }

    public IValidator<TOutput> Validator { get; }

    public IValidator<TOutput>? ValidatorFor(TState state) => null;

    public IReadOnlyList<AgentOutputExample<TOutput>> Examples(TState state) => [];
}

public sealed record AgentOutputExample<TOutput>(string Input, TOutput Output);
