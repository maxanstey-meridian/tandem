using System.Text.Json;
using FluentValidation;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public static class StructuredOutputPolicy
{
    public static StructuredOutputResult<TState> Parse<T, TState>(
        string response,
        PipelineMessage<TState> message,
        JsonSerializerOptions options,
        IValidator<T> validator,
        Func<T, PipelineMessage<TState>, StructuredOutcome<TState>> map
    )
    {
        string json;
        try
        {
            json = StructuredJsonExtractor.Extract(response);
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
            return new StructuredOutputResult<TState>(
                null,
                validation
                    .Errors.Select(error => new StructuredOutputProblem(
                        ToCamelCase(error.PropertyName),
                        error.ErrorMessage
                    ))
                    .ToArray(),
                response,
                value
            );
        }

        return new StructuredOutputResult<TState>(map(value, message), [], response, value);
    }

    private static StructuredOutputResult<TState> Failure<TState>(
        string raw,
        string field,
        string message
    ) => new(null, [new StructuredOutputProblem(field, message)], raw);

    private static string ToCamelCase(string path) =>
        string.IsNullOrEmpty(path) ? path : char.ToLowerInvariant(path[0]) + path[1..];
}

public static class StructuredJsonExtractor
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
