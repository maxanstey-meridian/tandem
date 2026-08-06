using System.Reflection;

namespace Tandem.Infrastructure;

public static class TandemHarnessInstructions
{
    private const string ResourceName = "Tandem.TANDEM.md";
    private static readonly Lazy<string> _value = new(Load);

    public static string Value => _value.Value;

    private static string Load()
    {
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded harness instructions '{ResourceName}' were not found."
            );
        using var reader = new StreamReader(stream);
        var value = reader.ReadToEnd().Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("Embedded TANDEM.md must not be empty.")
            : value;
    }
}
