using System.Text.Json;
using FluentValidation;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tandem.Actions;

public sealed class McpValidationFilter(McpToolContractRegistry registry)
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public McpRequestFilter<CallToolRequestParams, CallToolResult> Create() =>
        next =>
            async (context, cancellationToken) =>
            {
                var request = context.Params;
                if (request is null || !registry.TryGet(request.Name, out var contract))
                {
                    return await next(context, cancellationToken);
                }

                object? value;
                try
                {
                    var arguments = JsonSerializer.Serialize(request.Arguments, _jsonOptions);
                    value = JsonSerializer.Deserialize(
                        arguments,
                        contract.RequestType,
                        _jsonOptions
                    );
                }
                catch (JsonException exception)
                {
                    return Error(contract.ErrorIdentity, [new("$", exception.Message)]);
                }

                if (value is null)
                {
                    return Error(contract.ErrorIdentity, [new("$", "Request must not be null.")]);
                }

                var validator =
                    (IValidator?)context.Services?.GetService(contract.ValidatorType)
                    ?? throw new InvalidOperationException(
                        $"Validator {contract.ValidatorType.Name} is not registered."
                    );
                var result = await validator.ValidateAsync(
                    new ValidationContext<object>(value),
                    cancellationToken
                );
                if (!result.IsValid)
                {
                    return Error(
                        contract.ErrorIdentity,
                        result.Errors.Select(failure => new ValidationProblem(
                            ToCamelCase(failure.PropertyName),
                            failure.ErrorMessage
                        ))
                    );
                }

                return await next(context, cancellationToken);
            };

    private static CallToolResult Error(string identity, IEnumerable<ValidationProblem> problems)
    {
        var payload = JsonSerializer.Serialize(
            new { error = identity, problems = problems.ToArray() },
            _jsonOptions
        );
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = payload }],
        };
    }

    private static string ToCamelCase(string path) =>
        string.IsNullOrEmpty(path) ? path : char.ToLowerInvariant(path[0]) + path[1..];

    private sealed record ValidationProblem(string Field, string Message);
}
