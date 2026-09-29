using System.Text;
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
        T? value;
        try
        {
            value = AgentStructuredJsonExtractor.Extract(response).Deserialize<T>(options);
        }
        catch (InvalidOperationException exception)
        {
            return Failure<TState>(response, "$", exception.Message);
        }
        catch (JsonException exception)
        {
            return Failure<TState>(response, exception.Path ?? "$", exception.Message);
        }

        if (value is null)
        {
            return Failure<TState>(response, "$", "Response must contain a JSON object.");
        }

        return Validate<T, TState>(response, value, options, validator, contextualValidator);
    }

    private static AgentStructuredOutputResult<TState> Failure<TState>(
        string raw,
        string field,
        string message
    ) => new(null, [new AgentStructuredOutputProblem(field, message)], raw);

    /// <summary>
    /// Parses a free-text model response through the raw output definition, then applies
    /// the same intrinsic and contextual validation chain as JSON-schema outputs.
    /// </summary>
    public static AgentStructuredOutputResult<TState> ParseRaw<T, TState>(
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
            return Failure<TState>(response, "$", exception.Message);
        }

        if (value is null)
        {
            return Failure<TState>(response, "$", "Response did not produce an output value.");
        }

        return Validate<T, TState>(
            response,
            value,
            TandemJson.TypedContract,
            validator,
            contextualValidator
        );
    }

    private static AgentStructuredOutputResult<TState> Validate<T, TState>(
        string response,
        T value,
        JsonSerializerOptions options,
        IValidator<T> validator,
        IValidator<T>? contextualValidator
    )
    {
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

    private static string ToCamelCase(string path) =>
        string.IsNullOrEmpty(path) ? path : char.ToLowerInvariant(path[0]) + path[1..];
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
