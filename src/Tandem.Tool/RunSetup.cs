namespace Tandem.Application;

public sealed record RunPaths(Guid RunId, string RunDirectory, string WorkspacePath);

public sealed class RunSetup
{
    public RunPaths Create(string tandemHome)
    {
        var runId = Guid.CreateVersion7();
        var runDir = Path.Combine(tandemHome, "runs", runId.ToString("N"));
        var workspace = Path.Combine(runDir, "workspace");
        Directory.CreateDirectory(runDir);
        return new RunPaths(runId, runDir, workspace);
    }
}
