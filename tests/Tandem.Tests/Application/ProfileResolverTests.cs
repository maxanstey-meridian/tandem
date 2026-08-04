using FluentAssertions;
using Tandem.Application;
using Tandem.Domain;

namespace Tandem.Tests.Application;

public sealed class ProfileResolverTests
{
    private static TandemConfig BuildConfig()
    {
        var providers = new Dictionary<string, ProviderConfig>
        {
            ["openrouter"] = new(
                "openai",
                "https://openrouter.ai/api/v1",
                "OPENROUTER_API_KEY",
                WireApi.Completions
            ),
            ["local"] = new("openai", "http://127.0.0.1:10531/v1", null, WireApi.Responses),
        };
        var profiles = new Dictionary<string, ProfileConfig>
        {
            ["impl"] = new(
                "openrouter",
                "anthropic/claude-sonnet-4.5",
                ReasoningLevel.Medium,
                200000,
                32000,
                80
            ),
            ["local"] = new("local", "gpt-4o", null, 100000, 8000, 75),
        };
        return new TandemConfig(providers, profiles);
    }

    [Fact]
    public void ResolvesProfileWithApiKey()
    {
        var config = BuildConfig();
        var resolver = new ProfileResolver();
        var resolved = resolver.Resolve(config, "impl", "sk-or-key");

        resolved.ProviderName.Should().Be("openrouter");
        resolved.BaseUrl.Should().Be("https://openrouter.ai/api/v1");
        resolved.Model.Should().Be("anthropic/claude-sonnet-4.5");
        resolved.WireApi.Should().Be(WireApi.Completions);
        resolved.Reasoning.Should().Be(ReasoningLevel.Medium);
        resolved.ContextWindowTokens.Should().Be(200000);
        resolved.MaxOutputTokens.Should().Be(32000);
        resolved.CheckpointAtPercent.Should().Be(80);
    }

    [Fact]
    public void ResolvesLocalProfileWithoutApiKey()
    {
        var config = BuildConfig();
        var resolver = new ProfileResolver();
        var resolved = resolver.Resolve(config, "local", "");

        resolved.ProviderName.Should().Be("local");
        resolved.WireApi.Should().Be(WireApi.Responses);
        resolved.Reasoning.Should().BeNull();
    }

    [Fact]
    public void MissingProfileFails()
    {
        var config = BuildConfig();
        var resolver = new ProfileResolver();
        var act = () => resolver.Resolve(config, "nonexistent", "key");

        act.Should().Throw<ProfileResolutionException>().WithMessage("*not configured*");
    }

    [Fact]
    public void AbsentApiKeyFailsAndDoesNotLeakOtherEnvironmentValues()
    {
        var sentinelName = "TANDEM_TEST_SENTINEL_" + Guid.NewGuid().ToString("N");
        var sentinelValue = "secret-value-should-not-leak-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(sentinelName, sentinelValue);
        try
        {
            var config = BuildConfig();
            var resolver = new ProfileResolver();
            var act = () => resolver.Resolve(config, "impl", "");

            var ex = act.Should().Throw<ProfileResolutionException>().Which;
            ex.Message.Should().Contain("OPENROUTER_API_KEY");
            ex.Message.Should().NotContain(sentinelName);
            ex.Message.Should().NotContain(sentinelValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelName, null);
        }
    }

    [Fact]
    public void AbsentApiKeyErrorMentionsConfigVariableNameOnly()
    {
        var config = BuildConfig();
        var resolver = new ProfileResolver();

        var hadKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") is not null;
        var saved = hadKey ? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") : null;
        var probe = "probe-" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", probe);
        try
        {
            var ex = Assert.Throws<ProfileResolutionException>(() =>
                resolver.Resolve(config, "impl", "")
            );
            ex.Message.Should().Contain("OPENROUTER_API_KEY");
            ex.Message.Should().NotContain(probe);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", hadKey ? saved : null);
        }
    }
}
