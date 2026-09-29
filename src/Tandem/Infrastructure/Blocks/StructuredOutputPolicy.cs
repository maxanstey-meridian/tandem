using System.Text;
using System.Text.Json;
using FluentValidation;
using Tandem.Domain;

namespace Tandem;

internal static class AgentStructuredOutputPolicy
{
    public static ParsedOutput<T> Parse<T>(
        string response,
        IValidator<T> validator,
        IValidator<T>? contextualValidator = null
    )
    {
        T? value;
        try
        {
            value = AgentStructuredJsonExtractor
                .Extract(response)
                .Deserialize<T>(TandemJson.TypedContract);
        }
        catch (InvalidOperationException exception)
        {
            return ParsedOutput<T>.Invalid("$", exception.Message);
        }
        catch (JsonException exception)
        {
            return ParsedOutput<T>.Invalid(exception.Path ?? "$", exception.Message);
        }

        return value is null
            ? ParsedOutput<T>.Invalid("$", "Response must contain a JSON object.")
            : Validate(value, validator, contextualValidator);
    }

    /// <summary>
    /// Parses a free-text model response through the raw output definition, then applies
    /// the same intrinsic and contextual validation chain as JSON-schema outputs.
    /// </summary>
    public static ParsedOutput<T> ParseRaw<T>(
        string response,
        Func<string, T> parse,
        IValidator<T> validator,
        IValidator<T>? contextualValidator = null
    )
    {
        T? value;
        try
        {
            value = parse(response);
        }
        catch (InvalidOperationException exception)
        {
            return ParsedOutput<T>.Invalid("$", exception.Message);
        }

        return value is null
            ? ParsedOutput<T>.Invalid("$", "Response did not produce an output value.")
            : Validate(value, validator, contextualValidator);
    }

    private static ParsedOutput<T> Validate<T>(
        T value,
        IValidator<T> validator,
        IValidator<T>? contextualValidator
    )
    {
        var problems = ValidationProblem.From(validator.Validate(value));
        if (problems.Count == 0 && contextualValidator is not null)
        {
            problems = ValidationProblem.From(contextualValidator.Validate(value));
        }
        return problems.Count > 0
            ? ParsedOutput<T>.Invalid(problems)
            : ParsedOutput<T>.Valid(value);
    }
}

internal static class AgentStructuredJsonExtractor
{
    public static JsonElement Extract(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            throw new InvalidOperationException("Model response contains no JSON object.");
        }

        var reader = new Utf8JsonReader(
            Encoding.UTF8.GetBytes(text[start..]),
            new JsonReaderOptions { AllowMultipleValues = true }
        );
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }
}
