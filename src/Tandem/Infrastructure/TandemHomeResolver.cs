namespace Tandem.Infrastructure;

public static class TandemHomeResolver
{
    public static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("TANDEM_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Tandem"
        );
    }
}
