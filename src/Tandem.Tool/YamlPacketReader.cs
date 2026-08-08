using Tandem.Domain;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Tandem.Interfaces;

public sealed class YamlPacketReader
{
    private readonly IDeserializer _yaml = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .Build();

    public Packet Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new PacketException($"Packet file not found: {path}");
        }

        var raw = File.ReadAllText(path);
        var (frontmatter, body) = SplitFrontmatter(raw, path);
        var dto =
            _yaml.Deserialize<PacketYaml>(frontmatter)
            ?? throw new PacketException($"Packet frontmatter is empty in {path}.");

        var outcomes = (dto.Outcomes ?? [])
            .Select(o => new Outcome(
                ValidateNonEmpty(o.Id ?? string.Empty, "outcome id", path),
                ValidateNonEmpty(o.Description ?? string.Empty, "outcome description", path)
            ))
            .ToList();

        if (outcomes.Count == 0)
        {
            throw new PacketException($"Packet must declare at least one outcome in {path}.");
        }

        if (outcomes.Select(o => o.Id).Distinct().Count() != outcomes.Count)
        {
            throw new PacketException($"Outcome IDs must be unique in {path}.");
        }

        var title = ValidateNonEmpty(dto.Title ?? string.Empty, "title", path);
        var repository = ValidateNonEmpty(dto.Repository ?? string.Empty, "repository", path);
        if (!Path.IsPathRooted(repository))
        {
            repository = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, repository));
        }

        if (!Directory.Exists(repository))
        {
            throw new PacketException($"Packet repository does not exist: {repository}");
        }

        var baseRef = ValidateNonEmpty(dto.Base ?? string.Empty, "base", path);

        return new Packet(
            Title: title,
            Repository: repository,
            Base: baseRef,
            Outcomes: outcomes,
            Verification: dto.Verification ?? [],
            Constraints: dto.Constraints ?? [],
            ImplementationContext: body.Trim()
        );
    }

    private static (string Frontmatter, string Body) SplitFrontmatter(string raw, string path)
    {
        if (!raw.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw new PacketException($"Packet must start with YAML frontmatter '---' in {path}.");
        }

        var rest = raw.Substring(4);
        var end = rest.IndexOf("\n---\n", StringComparison.Ordinal);
        string front;
        string body;
        if (end >= 0)
        {
            front = rest[..end];
            body = rest[(end + 5)..];
        }
        else
        {
            // The closing '---' may be the last line with no trailing newline.
            var closing = rest.IndexOf("\n---", StringComparison.Ordinal);
            if (closing < 0 || closing + 4 != rest.Length)
            {
                throw new PacketException(
                    $"Packet frontmatter is not closed with '---' in {path}."
                );
            }
            front = rest[..closing];
            body = string.Empty;
        }

        return (front, body);
    }

    private static string ValidateNonEmpty(string value, string field, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new PacketException($"Packet {field} is required in {path}.");
        }

        return value.Trim();
    }

    private sealed class PacketYaml
    {
        public string? Title { get; set; }
        public string? Repository { get; set; }
        public string? Base { get; set; }
        public List<OutcomeYaml>? Outcomes { get; set; }
        public List<string>? Verification { get; set; }
        public List<string>? Constraints { get; set; }
    }

    private sealed class OutcomeYaml
    {
        public string? Id { get; set; }
        public string? Description { get; set; }
    }
}

public sealed class PacketException(string message) : Exception(message);
