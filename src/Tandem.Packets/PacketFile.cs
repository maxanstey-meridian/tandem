using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentValidation;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Tandem.Packets;

public static class PacketFile
{
    internal const int MaximumSourceBytes = 1024 * 1024;
    internal const int MaximumDepth = 64;

    public static PacketFile<T> Parse<T>(string content, string? sourceName = null) =>
        ParseCore<T>(content, CreateSource(sourceName), null);

    public static PacketFile<T> Parse<T>(
        string content,
        IValidator<T> validator,
        string? sourceName = null
    ) =>
        ParseCore(
            content,
            CreateSource(sourceName),
            validator ?? throw new ArgumentNullException(nameof(validator))
        );

    public static PacketFile<T> Read<T>(string path) =>
        ParseCore<T>(ReadText(path), CreateFileSource(path), null);

    public static PacketFile<T> Read<T>(string path, IValidator<T> validator) =>
        ParseCore(
            ReadText(path),
            CreateFileSource(path),
            validator ?? throw new ArgumentNullException(nameof(validator))
        );

    public static async ValueTask<PacketFile<T>> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken = default
    ) => ParseCore<T>(await ReadTextAsync(path, cancellationToken), CreateFileSource(path), null);

    public static async ValueTask<PacketFile<T>> ReadAsync<T>(
        string path,
        IValidator<T> validator,
        CancellationToken cancellationToken = default
    ) =>
        ParseCore(
            await ReadTextAsync(path, cancellationToken),
            CreateFileSource(path),
            validator ?? throw new ArgumentNullException(nameof(validator))
        );

    private static PacketFile<T> ParseCore<T>(
        string content,
        PacketSource source,
        IValidator<T>? validator
    )
    {
        ArgumentNullException.ThrowIfNull(content);
        EnsureSize(Encoding.UTF8.GetByteCount(content), source.Name);
        var normalized = NormalizeLines(content);
        var (yaml, context, frontmatterLine) = SplitEnvelope(normalized, source.Name);
        var nodes = new Dictionary<string, YamlNode>(StringComparer.Ordinal);

        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(yaml));
            if (stream.Documents.Count != 1)
            {
                throw Failure(
                    source.Name,
                    "$",
                    "Frontmatter must contain exactly one YAML document."
                );
            }

            var normalizedValue = NormalizeNode(
                stream.Documents[0].RootNode,
                "$",
                0,
                frontmatterLine,
                source.Name,
                nodes
            );
            if (normalizedValue is not Dictionary<string, object?>)
            {
                throw Failure(source.Name, "$", "Frontmatter root must be a mapping.");
            }

            var json = JsonSerializer.Serialize(normalizedValue);
            var value = JsonSerializer.Deserialize<T>(json, _serializerOptions);
            if (value is null)
            {
                throw Failure(source.Name, "$", "Frontmatter cannot decode to null.");
            }

            if (validator is not null)
            {
                var result = validator.Validate(value);
                if (!result.IsValid)
                {
                    var problems = result
                        .Errors.Select(error => new PacketProblem(
                            ToPacketPath(error.PropertyName),
                            error.ErrorMessage
                        ))
                        .ToArray();
                    throw new PacketFileException(
                        "Packet validation failed.",
                        source.Name,
                        problems
                    );
                }
            }

            return new PacketFile<T>(value, context.Trim(), source);
        }
        catch (PacketFileException)
        {
            throw;
        }
        catch (YamlException exception)
        {
            throw new PacketFileException(
                "Packet YAML is invalid.",
                source.Name,
                [
                    new PacketProblem(
                        "$",
                        exception.Message,
                        checked((int)exception.Start.Line) + frontmatterLine,
                        checked((int)exception.Start.Column)
                    ),
                ],
                exception
            );
        }
        catch (JsonException exception)
        {
            var path = exception.Message.Contains(
                "could not be mapped",
                StringComparison.OrdinalIgnoreCase
            )
                ? "$"
                : ToPacketPath(exception.Path);
            nodes.TryGetValue(path, out var node);
            throw new PacketFileException(
                "Packet shape is invalid.",
                source.Name,
                [
                    new PacketProblem(
                        path,
                        ShapeMessage(exception),
                        node is null ? null : checked((int)node.Start.Line) + frontmatterLine,
                        node is null ? null : checked((int)node.Start.Column)
                    ),
                ],
                exception
            );
        }
    }

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        NumberHandling = JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static object? NormalizeNode(
        YamlNode node,
        string path,
        int depth,
        int lineOffset,
        string? sourceName,
        IDictionary<string, YamlNode> nodes
    )
    {
        if (depth > MaximumDepth)
        {
            throw Failure(
                sourceName,
                "$",
                $"YAML nesting exceeds the maximum depth of {MaximumDepth}."
            );
        }
        nodes[path] = node;

        RejectUnsupportedNode(node, path, lineOffset, sourceName);
        return node switch
        {
            YamlMappingNode mapping => NormalizeMapping(
                mapping,
                path,
                depth,
                lineOffset,
                sourceName,
                nodes
            ),
            YamlSequenceNode sequence => sequence
                .Children.Select(
                    (child, index) =>
                        NormalizeNode(
                            child,
                            $"{path}[{index}]",
                            depth + 1,
                            lineOffset,
                            sourceName,
                            nodes
                        )
                )
                .ToList(),
            YamlScalarNode scalar => NormalizeScalar(scalar, path, lineOffset, sourceName),
            _ => throw Failure(sourceName, path, "Unsupported YAML value."),
        };
    }

    private static Dictionary<string, object?> NormalizeMapping(
        YamlMappingNode mapping,
        string path,
        int depth,
        int lineOffset,
        string? sourceName,
        IDictionary<string, YamlNode> nodes
    )
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is not YamlScalarNode keyNode)
            {
                throw Problem(
                    sourceName,
                    path,
                    "Mapping keys must be nonempty strings.",
                    pair.Key,
                    lineOffset
                );
            }
            RejectUnsupportedNode(keyNode, path, lineOffset, sourceName);
            if (
                NormalizeScalar(keyNode, path, lineOffset, sourceName) is not string key
                || key.Length == 0
            )
            {
                throw Problem(
                    sourceName,
                    path,
                    "Mapping keys must be nonempty strings.",
                    pair.Key,
                    lineOffset
                );
            }
            var childPath = path == "$" ? $"$.{key}" : $"{path}.{key}";
            if (
                !result.TryAdd(
                    key,
                    NormalizeNode(pair.Value, childPath, depth + 1, lineOffset, sourceName, nodes)
                )
            )
            {
                throw Problem(sourceName, childPath, "Duplicate mapping key.", keyNode, lineOffset);
            }
        }
        return result;
    }

    private static object? NormalizeScalar(
        YamlScalarNode scalar,
        string path,
        int lineOffset,
        string? sourceName
    )
    {
        var value = scalar.Value ?? "";
        var tag = scalar.Tag.IsEmpty || scalar.Tag.IsNonSpecific ? "" : scalar.Tag.Value;
        var plain = scalar.Style == ScalarStyle.Plain;
        if (
            tag.EndsWith(":null", StringComparison.Ordinal)
            || plain && (value is "null" or "Null" or "NULL" or "~")
        )
        {
            return null;
        }
        if (
            (tag.EndsWith(":bool", StringComparison.Ordinal) || plain)
            && bool.TryParse(value, out var boolean)
        )
        {
            return boolean;
        }
        if (
            (tag.EndsWith(":int", StringComparison.Ordinal) || plain)
            && long.TryParse(
                value.Replace("_", "", StringComparison.Ordinal),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var integer
            )
        )
        {
            return integer;
        }
        var normalizedNumber = value.Replace("_", "", StringComparison.Ordinal);
        if (
            tag.EndsWith(":float", StringComparison.Ordinal)
            || plain
                && double.TryParse(
                    normalizedNumber,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out _
                )
        )
        {
            if (
                !double.TryParse(
                    normalizedNumber,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number
                ) || !double.IsFinite(number)
            )
            {
                throw Problem(sourceName, path, "Numbers must be finite.", scalar, lineOffset);
            }
            return number;
        }
        if (plain && IsNonFiniteYamlNumber(value))
        {
            throw Problem(sourceName, path, "Numbers must be finite.", scalar, lineOffset);
        }
        return value;
    }

    private static bool IsNonFiniteYamlNumber(string value) =>
        value
            is ".inf"
                or ".Inf"
                or ".INF"
                or "-.inf"
                or "-.Inf"
                or "-.INF"
                or "+.inf"
                or "+.Inf"
                or "+.INF"
                or ".nan"
                or ".NaN"
                or ".NAN";

    private static void RejectUnsupportedNode(
        YamlNode node,
        string path,
        int lineOffset,
        string? sourceName
    )
    {
        if (!node.Anchor.IsEmpty)
        {
            throw Problem(
                sourceName,
                "$",
                "YAML anchors and aliases are not supported.",
                node,
                lineOffset
            );
        }
        var tag = node.Tag.IsEmpty || node.Tag.IsNonSpecific ? "" : node.Tag.Value;
        if (tag.Length > 0 && !tag.StartsWith("tag:yaml.org,2002:", StringComparison.Ordinal))
        {
            throw Problem(
                sourceName,
                path,
                "Custom YAML tags are not supported.",
                node,
                lineOffset
            );
        }
    }

    private static (string Yaml, string Context, int FrontmatterLine) SplitEnvelope(
        string content,
        string? sourceName
    )
    {
        var lines = content.Split('\n');
        if (lines.Length == 0 || lines[0] != "---")
        {
            throw Failure(sourceName, "$", "The first content line must be exactly '---'.", 1, 1);
        }
        var closing = Array.IndexOf(lines, "---", 1);
        if (closing < 0)
        {
            throw Failure(sourceName, "$", "A closing frontmatter delimiter is required.", 1, 1);
        }
        var yaml = string.Join('\n', lines[1..closing]);
        if (string.IsNullOrWhiteSpace(yaml))
        {
            throw Failure(
                sourceName,
                "$",
                "Frontmatter must contain a nonempty YAML mapping.",
                2,
                1
            );
        }
        return (yaml, string.Join('\n', lines[(closing + 1)..]), 1);
    }

    private static string NormalizeLines(string content) =>
        content
            .TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string ReadText(string path)
    {
        var source = CreateFileSource(path);
        try
        {
            using var stream = new FileStream(
                source.FullPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read
            );
            EnsureSize(stream.Length, source.Name);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true
            );
            return reader.ReadToEnd();
        }
        catch (PacketFileException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or DecoderFallbackException
            )
        {
            throw new PacketFileException(
                "Packet file could not be read.",
                source.Name,
                [new PacketProblem("$", exception.Message)],
                exception
            );
        }
    }

    private static async ValueTask<string> ReadTextAsync(
        string path,
        CancellationToken cancellationToken
    )
    {
        var source = CreateFileSource(path);
        try
        {
            await using var stream = new FileStream(
                source.FullPath!,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            EnsureSize(stream.Length, source.Name);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(false, true),
                detectEncodingFromByteOrderMarks: true
            );
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PacketFileException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or DecoderFallbackException
            )
        {
            throw new PacketFileException(
                "Packet file could not be read.",
                source.Name,
                [new PacketProblem("$", exception.Message)],
                exception
            );
        }
    }

    private static void EnsureSize(long bytes, string? sourceName)
    {
        if (bytes > MaximumSourceBytes)
        {
            throw Failure(
                sourceName,
                "$",
                $"Packet source exceeds the maximum size of {MaximumSourceBytes} bytes."
            );
        }
    }

    private static PacketSource CreateSource(string? sourceName) => new(sourceName, null, null);

    private static PacketSource CreateFileSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        return new PacketSource(path, fullPath, Path.GetDirectoryName(fullPath));
    }

    private static PacketFileException Failure(
        string? sourceName,
        string path,
        string message,
        int? line = null,
        int? column = null
    ) =>
        new(
            "Packet file is invalid.",
            sourceName,
            [new PacketProblem(path, message, line, column)]
        );

    private static PacketFileException Problem(
        string? sourceName,
        string path,
        string message,
        YamlNode node,
        int lineOffset
    ) =>
        Failure(
            sourceName,
            path,
            message,
            checked((int)node.Start.Line) + lineOffset,
            checked((int)node.Start.Column)
        );

    private static string ToPacketPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "$")
        {
            return "$";
        }
        if (path.StartsWith('$'))
        {
            return path;
        }
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => JsonNamingPolicy.SnakeCaseLower.ConvertName(segment));
        return "$." + string.Join('.', segments);
    }

    private static string ShapeMessage(JsonException exception)
    {
        if (exception.Message.Contains("System.String", StringComparison.Ordinal))
        {
            return "Value must be a string.";
        }
        if (exception.Message.Contains("System.Int32", StringComparison.Ordinal))
        {
            return "Value must be an integer.";
        }
        if (exception.Message.Contains("System.Boolean", StringComparison.Ordinal))
        {
            return "Value must be true or false.";
        }
        return "Value does not match the requested packet type.";
    }
}
