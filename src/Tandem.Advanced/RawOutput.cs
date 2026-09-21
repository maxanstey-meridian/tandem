using FluentValidation;
using Tandem.Domain;

namespace Tandem.Advanced;

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

public static class RawOutputExtensions
{
    /// <summary>
    /// Configures a raw-text structured-output contract. No response format is sent to
    /// the provider and no schema instructions are appended; the definition parses the
    /// free-text response and the validator chain runs on the parsed value.
    /// </summary>
    public static AgentBuilder<TState> WithRawOutput<TState, TOutput>(
        this AgentBuilder<TState> builder,
        IAgentRawOutputDefinition<TState, TOutput> output,
        Func<TState, TOutput, TState> apply,
        string? valueType = null
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(apply);
        return builder.ConfigureStructuredOutput(
            new AgentStructuredOutputDescriptor<TState>(
                (response, state) =>
                    AgentStructuredOutputPolicy.ParseRaw<TOutput, TState>(
                        response,
                        output.Parse,
                        output.Validator,
                        output.ValidatorFor(state)
                    ),
                Apply: (state, candidate) => apply(state, (TOutput)candidate),
                EmitAccepted: (runId, stepId, acceptedOutputId, kind, payload, candidate) =>
                    new OutputAccepted<TOutput>(
                        runId,
                        stepId,
                        acceptedOutputId,
                        kind,
                        valueType ?? typeof(TOutput).FullName,
                        payload,
                        (TOutput)candidate
                    ),
                OutputType: typeof(TOutput),
                ValueType: valueType,
                Instructions: output.Instructions
            ),
            options => options.ResponseFormat = null
        );
    }
}
