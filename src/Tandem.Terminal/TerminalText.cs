namespace Tandem.Terminal;

internal static class TerminalText
{
    public static string Sanitize(string value)
    {
        value = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("\t", "  ", StringComparison.Ordinal);
        return new string(
            value.Where(character => !char.IsControl(character) || character == '\n').ToArray()
        );
    }
}
