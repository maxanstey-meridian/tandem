using FluentValidation;

namespace Tandem.Infrastructure.Lifecycle;

public sealed record McpToolContract(
    string Name,
    Type RequestType,
    Type ValidatorType,
    string ErrorIdentity
);

public sealed class McpToolContractRegistry(IEnumerable<McpToolContract> contracts)
{
    private readonly IReadOnlyDictionary<string, McpToolContract> _contracts =
        contracts.ToDictionary(contract => contract.Name, StringComparer.Ordinal);

    public bool TryGet(string name, out McpToolContract contract) =>
        _contracts.TryGetValue(name, out contract!);
}

internal static class McpToolContractFactory
{
    public static McpToolContract Create<TRequest, TValidator>(string name)
        where TValidator : IValidator<TRequest> =>
        new(name, typeof(TRequest), typeof(TValidator), $"invalid {name} call");
}
