using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class RegistrationContractValidatorTests
{
    [Fact]
    public void AcceptsVersionTwoAgentWithOutputAndMultipleCapabilities()
    {
        var contract = RegistrationContractValidator.ParseAndValidate(ValidContract());

        Assert.Equal(2, contract.ContractVersion);
        Assert.Equal(2, contract.Nodes![0].Capabilities!.Length);
        Assert.NotNull(contract.Nodes[0].Output);
    }

    [Fact]
    public void RejectsDuplicateCapabilitiesAndMissingAuthoritativeValidation()
    {
        var value = ContractObject();
        var nodes = (object[])value["nodes"]!;
        var agent = (Dictionary<string, object?>)nodes[0];
        agent["capabilities"] = new object[]
        {
            Capability("same", validate: null),
            Capability("same", validate: "agent.second.validate"),
        };
        value["callbacks"] = new[]
        {
            "agent.message",
            "agent.output.validate",
            "agent.output.apply",
            "agent.first.apply",
            "agent.first.summary",
            "agent.second.validate",
            "agent.second.apply",
            "agent.second.summary",
            "done.summary",
        };

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("validateCallback is required", message);
        Assert.Contains("duplicates capability 'same'", message);
    }

    [Theory]
    [InlineData("ftp://localhost/v1", null, "absolute HTTP(S)")]
    [InlineData("https://example.com/v1", null, "required for non-loopback")]
    [InlineData("http://localhost/v1", "BAD-NAME", "valid environment-variable name")]
    public void RejectsUnsafeClientDescriptors(string endpoint, string? keyName, string expected)
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        agent["client"] = Client(endpoint, keyName);

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains(expected, message);
        Assert.DoesNotContain("secret-value", message);
    }

    [Fact]
    public void RejectsNonObjectRootSchemaAndVersionOne()
    {
        var value = ContractObject();
        value["contractVersion"] = 1;
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        var output = (Dictionary<string, object?>)agent["output"]!;
        output["jsonSchema"] = "{\"type\":\"array\"}";

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("contractVersion must be 2", message);
        Assert.Contains("object root with type 'object'", message);
    }

    private static string ValidContract() => JsonSerializer.Serialize(ContractObject());

    private static Dictionary<string, object?> ContractObject() =>
        new()
        {
            ["contractVersion"] = 2,
            ["name"] = "test",
            ["start"] = "agent",
            ["initialState"] = "{}",
            ["persist"] = false,
            ["callbacks"] = new[]
            {
                "agent.message",
                "agent.output.validate",
                "agent.output.apply",
                "agent.first.validate",
                "agent.first.apply",
                "agent.first.summary",
                "agent.second.validate",
                "agent.second.apply",
                "agent.second.summary",
                "done.summary",
            },
            ["nodes"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "agent",
                    ["kind"] = "agent",
                    ["instructions"] = "Test.",
                    ["messageCallback"] = "agent.message",
                    ["client"] = Client("http://127.0.0.1:10531/v1", null),
                    ["output"] = new Dictionary<string, object?>
                    {
                        ["jsonSchema"] = "{\"type\":\"object\"}",
                        ["validateCallback"] = "agent.output.validate",
                        ["applyCallback"] = "agent.output.apply",
                        ["contractName"] = "result",
                    },
                    ["capabilities"] = new object[]
                    {
                        Capability("first", "agent.first.validate"),
                        Capability("second", "agent.second.validate"),
                    },
                },
                new Dictionary<string, object?>
                {
                    ["id"] = "done",
                    ["kind"] = "completion",
                    ["summaryCallback"] = "done.summary",
                },
            },
            ["routes"] = new[]
            {
                new
                {
                    source = "agent",
                    target = "done",
                    label = "done",
                    outcome = "success",
                },
            },
            ["outputs"] = new[] { "done" },
        };

    private static Dictionary<string, object?> Client(string endpoint, string? keyName) =>
        new()
        {
            ["kind"] = "openai-compatible",
            ["version"] = 1,
            ["endpoint"] = endpoint,
            ["model"] = "model",
            ["wireApi"] = "responses",
            ["apiKeyEnvironmentVariable"] = keyName,
            ["verifyModel"] = false,
        };

    private static Dictionary<string, object?> Capability(string name, string? validate) =>
        new()
        {
            ["name"] = name,
            ["jsonSchema"] = "{\"type\":\"object\"}",
            ["validateCallback"] = validate,
            ["applyCallback"] = $"agent.{name}.apply",
            ["summaryCallback"] = $"agent.{name}.summary",
            ["contractName"] = $"capability.{name}",
        };
}
