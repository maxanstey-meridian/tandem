using FluentValidation;

namespace Tandem;

public interface IAgentOutputDefinition<TState, TOutput>
{
    public string Instructions { get; }

    public IValidator<TOutput> Validator { get; }

    public IValidator<TOutput>? ValidatorFor(TState state) => null;

    public IReadOnlyList<AgentOutputExample<TOutput>> Examples(TState state) => [];
}

/// <summary>
/// Defines a raw-text structured-output contract for providers that cannot honour
/// JSON-schema response formats. The model response is free text; <see cref="Parse"/>
/// converts it into the typed output, and the same validator chain applies afterwards.
/// Tandem sends no response format for this agent and appends no schema instructions.
/// </summary>
public interface IAgentRawOutputDefinition<TState, TOutput>
{
    public string Instructions { get; }

    /// <summary>Parses the complete model response into a candidate value.</summary>
    /// <exception cref="InvalidOperationException">Describes an unparseable response.</exception>
    public TOutput Parse(string response);

    public IValidator<TOutput> Validator { get; }

    public IValidator<TOutput>? ValidatorFor(TState state) => null;
}

public sealed record AgentOutputExample<TOutput>(string Input, TOutput Output);
