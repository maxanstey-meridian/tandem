using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Spectre.Console;

namespace Tandem.Terminal;

internal static class ToolStartFormatter
{
    private static readonly JsonSerializerOptions _displayJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Format(string name, string arguments, string? workingDirectory)
    {
        var output = new StringBuilder(name);
        if (arguments.Length > 0)
        {
            var root = TryParseArguments(arguments);
            AppendArguments(output, root, arguments);
        }
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            output.Append(" in ").Append(ContractHome(workingDirectory));
        }
        return output.ToString();
    }

    internal static string FormatMarkup(
        string value,
        bool includesToolName,
        bool includesWorkingDirectory
    )
    {
        var output = new StringBuilder();
        var argumentsStart = 0;
        if (includesToolName)
        {
            var toolEnd = value.IndexOf(' ');
            if (toolEnd < 0)
            {
                AppendStyled(output, value, "cornflowerblue");
                return output.ToString();
            }
            AppendStyled(output, value[..toolEnd], "cornflowerblue");
            argumentsStart = toolEnd;
        }

        var directoryStart = includesWorkingDirectory
            ? value.LastIndexOf(" in ", StringComparison.Ordinal)
            : -1;
        if (directoryStart >= argumentsStart)
        {
            AppendArgumentsMarkup(output, value[argumentsStart..directoryStart]);
            AppendStyled(output, " in ", "grey");
            AppendStyled(output, value[(directoryStart + 4)..], "mediumpurple1");
        }
        else if (includesWorkingDirectory && value.StartsWith("in ", StringComparison.Ordinal))
        {
            AppendStyled(output, "in ", "grey");
            AppendStyled(output, value[3..], "mediumpurple1");
        }
        else
        {
            AppendArgumentsMarkup(output, value[argumentsStart..]);
        }
        return output.ToString();
    }

    private static JsonElement? TryParseArguments(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void AppendArguments(StringBuilder output, JsonElement? root, string arguments)
    {
        if (root is { ValueKind: JsonValueKind.Object } objectRoot)
        {
            foreach (
                var property in objectRoot
                    .EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
            )
            {
                output
                    .Append(' ')
                    .Append(property.Name)
                    .Append('=')
                    .Append(JsonSerializer.Serialize(property.Value, _displayJson));
            }
        }
        else if (root is { ValueKind: not JsonValueKind.Undefined })
        {
            output.Append(" arguments=").Append(JsonSerializer.Serialize(root, _displayJson));
        }
        else
        {
            output.Append(" arguments=").Append(JsonSerializer.Serialize(arguments));
        }
    }

    private static void AppendArgumentsMarkup(StringBuilder output, string value)
    {
        var index = 0;
        while (index < value.Length)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                var end = index + 1;
                while (end < value.Length && char.IsWhiteSpace(value[end]))
                {
                    end++;
                }
                AppendStyled(output, value[index..end], "grey");
                index = end;
                continue;
            }

            var tokenEnd = value.IndexOf(' ', index);
            if (tokenEnd < 0)
            {
                tokenEnd = value.Length;
            }
            var equals = value.IndexOf('=', index, tokenEnd - index);
            if (equals > index)
            {
                AppendStyled(output, value[index..equals], "cyan");
                AppendStyled(output, "=", "grey");
                AppendStyled(output, value[(equals + 1)..tokenEnd], "yellow");
            }
            else
            {
                AppendStyled(output, value[index..tokenEnd], "yellow");
            }
            index = tokenEnd;
        }
    }

    private static void AppendStyled(StringBuilder output, string value, string color) =>
        output.Append('[').Append(color).Append(']').Append(Markup.Escape(value)).Append("[/]");

    private static string ContractHome(string workingDirectory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length == 0 || !Path.IsPathRooted(workingDirectory))
        {
            return workingDirectory;
        }

        var relative = Path.GetRelativePath(
            Path.GetFullPath(home),
            Path.GetFullPath(workingDirectory)
        );
        if (relative == ".")
        {
            return "~";
        }
        if (
            relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
        )
        {
            return $"~{Path.DirectorySeparatorChar}{relative}";
        }
        return workingDirectory;
    }
}
