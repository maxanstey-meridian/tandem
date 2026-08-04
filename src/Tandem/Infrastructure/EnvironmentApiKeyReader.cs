namespace Tandem.Infrastructure;

public static class EnvironmentApiKeyReader
{
    public static string Read(string? environmentVariable)
    {
        if (environmentVariable is null)
        {
            return string.Empty;
        }

        return Environment.GetEnvironmentVariable(environmentVariable) ?? string.Empty;
    }
}
