using System.Reflection;
using System.Text;

namespace Tandem.Delivery;

internal static class DeliveryHarnessInstructions
{
    private const string ResourceName = "Tandem.Delivery.DELIVERY_HARNESS.md";
    private static readonly Lazy<string> _value = new(Load);

    internal static string Value => _value.Value;

    private static string Load()
    {
        using var stream =
            Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded Delivery Harness instructions '{ResourceName}' were not found."
            );
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        var value = reader.ReadToEnd();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                "Embedded Delivery Harness instructions must not be empty."
            )
            : value;
    }
}
