using System.Text.Json;
using FluentValidation;
using Tandem.Domain;

namespace Tandem;

internal static class AgentStructuredOutputPolicy
{
    public static AgentStructuredOutputResult<TState> Parse<T, TState>(
        string response,
        JsonSerializerOptions options,
        IValidator<T> validator,
        IValidator<T>? contextualValidator = null
    )
    {
        string json;
        try
        {
            json = AgentStructuredJsonExtractor.Extract(response);
        }
        catch (InvalidOperationException exception)
        {
            return Failure<TState>(response, "$", exception.Message);
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, options);
        }
        catch (JsonException exception)
        {
            return Failure<TState>(response, exception.Path ?? "$", exception.Message);
        }

        if (value is null)
        {
            return Failure<TState>(response, "$", "Response must contain a JSON object.");
        }

        var validation = validator.Validate(value);
        if (!validation.IsValid)
        {
            return new AgentStructuredOutputResult<TState>(
                null,
                validation
                    .Errors.Select(error => new AgentStructuredOutputProblem(
                        ToCamelCase(error.PropertyName),
                        error.ErrorMessage
                    ))
                    .ToArray(),
                response,
                value
            );
        }

        if (contextualValidator is not null)
        {
            validation = contextualValidator.Validate(value);
            if (!validation.IsValid)
            {
                return new AgentStructuredOutputResult<TState>(
                    null,
                    validation
                        .Errors.Select(error => new AgentStructuredOutputProblem(
                            ToCamelCase(error.PropertyName),
                            error.ErrorMessage
                        ))
                        .ToArray(),
                    response,
                    value
                );
            }
        }

        return new AgentStructuredOutputResult<TState>(
            new AgentStructuredOutcome<TState>(
                StandardOutcomeKinds.Success,
                "Succeeded",
                JsonSerializer.SerializeToElement(value, options)
            ),
            [],
            response,
            value
        );
    }

    private static AgentStructuredOutputResult<TState> Failure<TState>(
        string raw,
        string field,
        string message
    ) => new(null, [new AgentStructuredOutputProblem(field, message)], raw);

    private static string ToCamelCase(string path) =>
        string.IsNullOrEmpty(path) ? path : char.ToLowerInvariant(path[0]) + path[1..];
}

internal static class AgentStructuredJsonExtractor
{
    public static string Extract(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            throw new InvalidOperationException("Model response contains no JSON object.");
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return text.Substring(start, index - start + 1);
            }
        }

        throw new InvalidOperationException("Model response contains incomplete JSON object.");
    }
}
