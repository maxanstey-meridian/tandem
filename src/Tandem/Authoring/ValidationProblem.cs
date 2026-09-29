using FluentValidation.Results;

namespace Tandem;

/// <summary>
/// One reason a model-authored or callback-supplied value was rejected. <see cref="Path"/> names
/// the offending member (<c>$</c> for the value as a whole).
/// </summary>
public sealed record ValidationProblem(string Path, string Message)
{
    internal static IReadOnlyList<ValidationProblem> From(ValidationResult validation) =>
        [
            .. validation.Errors.Select(error => new ValidationProblem(
                CamelCase(error.PropertyName),
                error.ErrorMessage
            )),
        ];

    private static string CamelCase(string path) =>
        string.IsNullOrEmpty(path) ? path : char.ToLowerInvariant(path[0]) + path[1..];
}
