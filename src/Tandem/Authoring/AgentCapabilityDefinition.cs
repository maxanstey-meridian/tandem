using FluentValidation;

namespace Tandem;

public interface IAgentCapabilityDefinition<TState, TRequest>
    where TRequest : class
{
    public string ToolName { get; }

    public string Instructions { get; }

    public IValidator<TRequest> Validator { get; }

    public IValidator<TRequest>? ValidatorFor(TState state) => null;

    public string Summarize(TRequest request);
}
