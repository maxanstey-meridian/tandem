using System.Text.Json;
using FluentValidation;
using Tandem.Domain;

namespace Tandem.Infrastructure.Blocks;

public static class StructuredOutputPolicy
{
    public static StructuredOutputResult Parse<T>(
        string response,
        PipelineContext context,
        JsonSerializerOptions options,
        IValidator<T> validator,
        Func<T, PipelineContext, StructuredOutcome> map
    )
    {
        string json;
        try
        {
            json = StructuredJsonExtractor.Extract(response);
        }
        catch (InvalidOperationException exception)
        {
            return Failure(response, "$", exception.Message);
        }

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(json, options);
        }
        catch (JsonException exception)
        {
            return Failure(response, exception.Path ?? "$", exception.Message);
        }

        if (value is null)
        {
            return Failure(response, "$", "Response must contain a JSON object.");
        }

        var validation = validator.Validate(value);
        if (!validation.IsValid)
        {
            return new StructuredOutputResult(
                null,
                validation
                    .Errors.Select(error => new StructuredOutputProblem(
                        ToCamelCase(error.PropertyName),
                        error.ErrorMessage
                    ))
                    .ToArray(),
                response
            );
        }

        return new StructuredOutputResult(map(value, context), [], response);
    }

    private static StructuredOutputResult Failure(string raw, string field, string message) =>
        new(null, [new StructuredOutputProblem(field, message)], raw);

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
