namespace Tandem;

public sealed class AgentSkill
{
    private AgentSkill(string directoryPath)
    {
        DirectoryPath = directoryPath;
    }

    public string DirectoryPath { get; }

    internal AgentSkillDescriptor Descriptor => new(DirectoryPath);

    public static AgentSkill FromDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException(
                $"Agent skill directory does not exist: {fullPath}"
            );
        }

        var skillPath = Path.Combine(fullPath, "SKILL.md");
        if (!File.Exists(skillPath))
        {
            throw new FileNotFoundException(
                $"Agent skill directory does not contain SKILL.md: {fullPath}",
                skillPath
            );
        }

        return new AgentSkill(fullPath);
    }
}

internal sealed record AgentSkillDescriptor(string DirectoryPath);
