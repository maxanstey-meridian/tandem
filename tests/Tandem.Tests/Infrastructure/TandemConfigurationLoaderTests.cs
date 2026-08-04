using FluentAssertions;
using Tandem.Domain;
using Tandem.Infrastructure;

namespace Tandem.Tests.Infrastructure;

public sealed class TandemConfigurationLoaderTests
{
    private const string TwoProviderConfig = """
        {
          "providers": {
            "openrouter": {
              "type": "openai",
              "baseUrl": "https://openrouter.ai/api/v1",
              "apiKeyEnvironmentVariable": "OPENROUTER_API_KEY",
              "wireApi": "completions"
            },
            "chatgpt": {
              "type": "openai",
              "baseUrl": "http://127.0.0.1:10531/v1",
              "wireApi": "completions"
            }
          },
          "profiles": {
            "implementation": {
              "provider": "openrouter",
              "model": "anthropic/claude-sonnet-4.5",
              "reasoningEffort": "medium",
              "contextWindowTokens": 200000,
              "maxOutputTokens": 32000,
              "checkpointAtPercent": 80
            },
            "local": {
              "provider": "chatgpt",
              "model": "gpt-4o",
              "contextWindowTokens": 100000,
              "maxOutputTokens": 8000,
              "checkpointAtPercent": 75
            }
          }
        }
        """;

    [Fact]
    public void LoadsTwoProvidersAndProfilesWithTypedValues()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(configPath, TwoProviderConfig);

        var config = new TandemConfigurationLoader().Load(temp.Dir);

        config.Providers.Should().HaveCount(2);
        config.Profiles.Should().HaveCount(2);

        var openrouter = config.Providers["openrouter"];
        openrouter.Type.Should().Be("openai");
        openrouter.BaseUrl.Should().Be("https://openrouter.ai/api/v1");
        openrouter.ApiKeyEnvironmentVariable.Should().Be("OPENROUTER_API_KEY");
        openrouter.WireApi.Should().Be(WireApi.Completions);

        var chatgpt = config.Providers["chatgpt"];
        chatgpt.ApiKeyEnvironmentVariable.Should().BeNull();

        var impl = config.Profiles["implementation"];
        impl.Provider.Should().Be("openrouter");
        impl.Model.Should().Be("anthropic/claude-sonnet-4.5");
        impl.ReasoningEffort.Should().Be(ReasoningLevel.Medium);
        impl.ContextWindowTokens.Should().Be(200000);
        impl.MaxOutputTokens.Should().Be(32000);
        impl.CheckpointAtPercent.Should().Be(80);

        var local = config.Profiles["local"];
        local.ReasoningEffort.Should().BeNull();
    }

    [Fact]
    public void MissingConfigFileFails()
    {
        using var temp = new TempDir();
        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*not found*");
    }

    [Fact]
    public void InvalidJsonFails()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(configPath, "{not json");

        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*not valid JSON*");
    }

    [Fact]
    public void RelativeBaseUrlFails()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(
            configPath,
            """
            {
              "providers": { "p": { "type": "openai", "baseUrl": "relative", "wireApi": "completions" } },
              "profiles": { "x": { "provider": "p", "model": "m", "contextWindowTokens": 1000, "maxOutputTokens": 100, "checkpointAtPercent": 80 } }
            }
            """
        );

        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*baseUrl*");
    }

    [Fact]
    public void OutputTokensNotBelowContextWindowFails()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(
            configPath,
            """
            {
              "providers": { "p": { "type": "openai", "baseUrl": "https://x.example/v1", "wireApi": "completions" } },
              "profiles": { "x": { "provider": "p", "model": "m", "contextWindowTokens": 1000, "maxOutputTokens": 1000, "checkpointAtPercent": 80 } }
            }
            """
        );

        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*below*");
    }

    [Fact]
    public void OutputTokensAboveContextWindowFails()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(
            configPath,
            """
            {
              "providers": { "p": { "type": "openai", "baseUrl": "https://x.example/v1", "wireApi": "completions" } },
              "profiles": { "x": { "provider": "p", "model": "m", "contextWindowTokens": 1000, "maxOutputTokens": 2000, "checkpointAtPercent": 80 } }
            }
            """
        );

        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*below*");
    }

    [Fact]
    public void CheckpointPercentOutOfRangeFails()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(
            configPath,
            """
            {
              "providers": { "p": { "type": "openai", "baseUrl": "https://x.example/v1", "wireApi": "completions" } },
              "profiles": { "x": { "provider": "p", "model": "m", "contextWindowTokens": 10000, "maxOutputTokens": 1000, "checkpointAtPercent": 40 } }
            }
            """
        );

        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*checkpointAtPercent*");
    }

    [Fact]
    public void InvalidWireApiFails()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(
            configPath,
            """
            {
              "providers": { "p": { "type": "openai", "baseUrl": "https://x.example/v1", "wireApi": "weird" } },
              "profiles": {}
            }
            """
        );

        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*wireApi*");
    }

    [Fact]
    public void InvalidReasoningEffortFails()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(
            configPath,
            """
            {
              "providers": { "p": { "type": "openai", "baseUrl": "https://x.example/v1", "wireApi": "completions" } },
              "profiles": { "x": { "provider": "p", "model": "m", "reasoningEffort": "ultra", "contextWindowTokens": 10000, "maxOutputTokens": 1000, "checkpointAtPercent": 80 } }
            }
            """
        );

        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*reasoningEffort*");
    }

    [Fact]
    public void ProfileReferencesUnknownProviderFails()
    {
        using var temp = new TempDir();
        var configPath = Path.Combine(temp.Dir, "config.json");
        File.WriteAllText(
            configPath,
            """
            {
              "providers": { "p": { "type": "openai", "baseUrl": "https://x.example/v1", "wireApi": "completions" } },
              "profiles": { "x": { "provider": "nope", "model": "m", "contextWindowTokens": 10000, "maxOutputTokens": 1000, "checkpointAtPercent": 80 } }
            }
            """
        );

        var act = () => new TandemConfigurationLoader().Load(temp.Dir);

        act.Should().Throw<ConfigurationLoadException>().WithMessage("*unknown provider*");
    }

    private sealed class TempDir : IDisposable
    {
        public string Dir { get; } =
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "tandem-cfg-" + Guid.NewGuid().ToString("N")
            );

        public TempDir() => Directory.CreateDirectory(Dir);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, true);
            }
            catch { }
        }
    }
}
