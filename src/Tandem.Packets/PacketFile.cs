using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using FluentValidation;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Tandem.Packets;

public static class PacketFile
{
    internal const int MaximumSourceBytes = 1024 * 1024;
    internal const int MaximumDepth = 64;

    public static PacketFile<T> Parse<T>(
        string content,
        IValidator<T>? validator = null,
        string? sourceName = null
    ) => ParseCore(content, CreateSource(sourceName), validator);

    public static async ValueTask<PacketFile<T>> ReadAsync<T>(
        string path,
        IValidator<T>? validator = null,
        CancellationToken cancellationToken = default
    ) => ParseCore(await ReadTextAsync(path, cancellationToken), CreateFileSource(path), validator);

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

            var json = ToJson(
                stream.Documents[0].RootNode,
                "$",
                0,
                frontmatterLine,
                source.Name,
                nodes
            );
            if (json is not JsonObject)
            {
                throw Failure(source.Name, "$", "Frontmatter root must be a mapping.");
            }

            var value = json.Deserialize<T>(_serializerOptions);
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
            var (path, message) = ShapeProblem(typeof(T), exception.Path ?? "$");
            nodes.TryGetValue(path, out var node);
            throw new PacketFileException(
                "Packet shape is invalid.",
                source.Name,
                [
                    new PacketProblem(
                        path,
                        message,
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
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    private static JsonNode? ToJson(
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
            YamlMappingNode mapping => ToJsonObject(
                mapping,
                path,
                depth,
                lineOffset,
                sourceName,
                nodes
            ),
            YamlSequenceNode sequence => new JsonArray([
                .. sequence.Children.Select(
                    (child, index) =>
                        ToJson(child, $"{path}[{index}]", depth + 1, lineOffset, sourceName, nodes)
                ),
            ]),
            YamlScalarNode scalar => ToJsonValue(scalar, path, lineOffset, sourceName),
            _ => throw Failure(sourceName, path, "Unsupported YAML value."),
        };
    }

    private static JsonObject ToJsonObject(
        YamlMappingNode mapping,
        string path,
        int depth,
        int lineOffset,
        string? sourceName,
        IDictionary<string, YamlNode> nodes
    )
    {
        var result = new JsonObject();
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode { Value: "<<", Style: ScalarStyle.Plain })
            {
                throw Problem(
                    sourceName,
                    path,
                    "YAML merge keys are not supported.",
                    pair.Key,
                    lineOffset
                );
            }
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
                ToJsonValue(keyNode, path, lineOffset, sourceName) is not { } keyValue
                || !keyValue.TryGetValue(out string? key)
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
            if (result.ContainsKey(key))
            {
                throw Problem(sourceName, childPath, "Duplicate mapping key.", keyNode, lineOffset);
            }
            result[key] = ToJson(pair.Value, childPath, depth + 1, lineOffset, sourceName, nodes);
        }
        return result;
    }

    // YAML 1.2 core schema resolution, the same schema tandem-ts reads packets with.
    private static readonly Regex _coreNull = new(@"^(?:null|Null|NULL|~|)$");
    private static readonly Regex _coreBool = new(@"^(?:true|True|TRUE|false|False|FALSE)$");
    private static readonly Regex _coreInt = new(@"^[-+]?[0-9]+$");
    private static readonly Regex _coreFloat = new(
        @"^[-+]?(?:\.[0-9]+|[0-9]+(?:\.[0-9]*)?)(?:[eE][-+]?[0-9]+)?$"
    );
    private static readonly Regex _coreNonFinite = new(
        @"^(?:[-+]?\.(?:inf|Inf|INF)|\.nan|\.NaN|\.NAN)$"
    );

    private static JsonValue? ToJsonValue(
        YamlScalarNode scalar,
        string path,
        int lineOffset,
        string? sourceName
    )
    {
        var value = scalar.Value ?? "";
        var tag = scalar.Tag.IsEmpty || scalar.Tag.IsNonSpecific ? "" : scalar.Tag.Value;
        var plain = scalar.Style == ScalarStyle.Plain;
        bool Is(string type, Regex pattern) =>
            tag.EndsWith(type, StringComparison.Ordinal)
            || plain && tag.Length == 0 && pattern.IsMatch(value);

        if (Is(":null", _coreNull))
        {
            return null;
        }
        if (Is(":bool", _coreBool))
        {
            return JsonValue.Create(value is "true" or "True" or "TRUE");
        }
        if (
            Is(":int", _coreInt)
            && long.TryParse(value, CultureInfo.InvariantCulture, out var integer)
        )
        {
            return JsonValue.Create(integer);
        }
        if (Is(":float", _coreNonFinite) && _coreNonFinite.IsMatch(value))
        {
            throw Problem(sourceName, path, "Numbers must be finite.", scalar, lineOffset);
        }
        if (Is(":float", _coreFloat) || Is(":int", _coreInt))
        {
            return
                double.TryParse(value, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number)
                ? JsonValue.Create(number)
                : throw Problem(sourceName, path, "Numbers must be finite.", scalar, lineOffset);
        }
        return JsonValue.Create(value);
    }

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
        var segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(segment => JsonNamingPolicy.SnakeCaseLower.ConvertName(segment));
        return "$." + string.Join('.', segments);
    }

    // Maps a deserialization failure to the packet path and the expected type, using the
    // serializer's own contract metadata for the requested packet type.
    private static (string Path, string Message) ShapeProblem(Type packetType, string jsonPath)
    {
        var info = _serializerOptions.GetTypeInfo(packetType);
        foreach (var segment in _jsonPathSegment.Matches(jsonPath).Select(match => match.Groups[1]))
        {
            Type? next;
            if (!segment.Success)
            {
                next = info.ElementType;
            }
            else
            {
                var property = info.Properties.FirstOrDefault(candidate =>
                    candidate.Name == segment.Value
                );
                if (property is null)
                {
                    return ("$", $"Unknown key '{segment.Value}'.");
                }
                next = property.PropertyType;
            }
            if (next is null)
            {
                break;
            }
            info = _serializerOptions.GetTypeInfo(Nullable.GetUnderlyingType(next) ?? next);
        }
        var message = info.Type switch
        {
            var type when type == typeof(string) => "Value must be a string.",
            var type when type == typeof(bool) => "Value must be true or false.",
            var type
                when type == typeof(int)
                    || type == typeof(long)
                    || type == typeof(short)
                    || type == typeof(byte) => "Value must be an integer.",
            var type when type.IsEnum => "Value must be one of the allowed names.",
            _ => "Value does not match the requested packet type.",
        };
        return (jsonPath, message);
    }

    private static readonly Regex _jsonPathSegment = new(@"\.([^.\[]+)|\[\d+\]");
}
