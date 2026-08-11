using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class RegistrationContractValidatorTests
{
    [Fact]
    public void AcceptsVersionSixAgentWithOutputCapabilitiesAndSkills()
    {
        var contract = RegistrationContractValidator.ParseAndValidate(ValidContract());

        Assert.Equal(6, contract.ContractVersion);
        Assert.Equal(2, contract.Nodes![0].Capabilities!.Length);
        Assert.Single(contract.Nodes[0].SkillDirectories!);
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

        Assert.Contains("contractVersion must be 6", message);
        Assert.Contains("object root with type 'object'", message);
    }

    [Fact]
    public void RejectsTerminalStart()
    {
        var value = ContractObject();
        value["start"] = "done";

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("start node 'done' cannot be a terminal", message);
    }

    [Fact]
    public void RejectsEffectivePersistenceWithoutLedgerPath()
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        agent["persist"] = true;

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("ledgerPath is required when persistence is enabled", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("terminal")]
    public void AcceptsSupportedPresentation(string? presentation)
    {
        var value = ContractObject();
        value["presentation"] = presentation;

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.Equal(presentation, contract.Presentation);
    }

    [Fact]
    public void AcceptsOptionalObservationCallback()
    {
        var value = ContractObject();
        value["observationCallback"] = "c20";

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.Equal("c20", contract.ObservationCallback);
    }

    [Fact]
    public void RejectsBlankOrDuplicateObservationCallback()
    {
        var blank = ContractObject();
        blank["observationCallback"] = " ";
        var blankMessage = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(blank))
            )
            .Message;
        Assert.Contains("observationCallback must be non-blank", blankMessage);

        var duplicate = ContractObject();
        duplicate["observationCallback"] = "agent.message";
        var duplicateMessage = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(duplicate))
            )
            .Message;
        Assert.Contains("duplicates callback reference 'agent.message'", duplicateMessage);
        Assert.Contains("observationCallback", duplicateMessage);
    }

    [Fact]
    public void RejectsUnsupportedPresentation()
    {
        var value = ContractObject();
        value["presentation"] = "events";

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("presentation must be null or 'terminal'", message);
    }

    [Fact]
    public void RejectsMultipleUnconditionalRoutesForOneOutcome()
    {
        var value = ContractObject();
        value["routes"] = new object[]
        {
            new
            {
                source = "agent",
                target = "done",
                label = "first",
                outcome = "success",
            },
            new
            {
                source = "agent",
                target = "done",
                label = "second",
                outcome = "success",
            },
        };

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("more than one unconditional route", message);
    }

    [Fact]
    public void RejectsReachableTerminalMissingFromOutputsAndUnreachableOutput()
    {
        var value = ContractObject();
        var nodes = ((object[])value["nodes"]!).ToList();
        nodes.Add(
            new Dictionary<string, object?>
            {
                ["id"] = "unused",
                ["kind"] = "failure",
                ["summaryCallback"] = "unused.summary",
            }
        );
        value["nodes"] = nodes;
        value["outputs"] = new[] { "unused" };

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("reachable terminal 'done' must be listed", message);
        Assert.Contains("output 'unused' is unreachable", message);
    }

    [Fact]
    public void AcceptsRunLocalInteractionHandlerBinding()
    {
        var value = InteractionContractObject();
        value["interactionHandlers"] = new[]
        {
            new
            {
                id = "review-handler",
                target = "review",
                handleCallback = "c2",
            },
        };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        var binding = Assert.Single(contract.InteractionHandlers!);
        Assert.Equal("review", binding.Target);
        Assert.Equal("c2", binding.HandleCallback);
    }

    [Fact]
    public void AllowsMissingInteractionHandlerBinding()
    {
        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(InteractionContractObject())
        );

        Assert.Null(contract.InteractionHandlers);
    }

    [Fact]
    public void RejectsInvalidInteractionHandlerBindings()
    {
        var value = InteractionContractObject();
        value["interactionHandlers"] = new object[]
        {
            new
            {
                id = "handler",
                target = "missing",
                handleCallback = "c2",
            },
            new
            {
                id = "handler",
                target = "done",
                handleCallback = " ",
            },
            new
            {
                id = "first-target",
                target = "ask",
                handleCallback = "c20",
            },
            new
            {
                id = "second-target",
                target = "ask",
                handleCallback = "c21",
            },
        };

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("duplicates interaction handler ID 'handler'", message);
        Assert.Contains("references unknown node 'missing'", message);
        Assert.Contains("node 'done' must be an interaction", message);
        Assert.Contains("handleCallback is required", message);
        Assert.Contains("duplicates interaction handler target 'ask'", message);
    }

    [Fact]
    public void RejectsDuplicateCallbackReferencesAcrossOneGlobalNamespace()
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        var output = (Dictionary<string, object?>)agent["output"]!;
        output["validateForCallback"] = "agent.message";

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains(
            "output.validateForCallback duplicates callback reference 'agent.message'",
            message
        );
        Assert.Contains("from nodes[0].messageCallback", message);
    }

    [Fact]
    public void RequiresAuthoredOutputAndCapabilityInstructions()
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        var output = (Dictionary<string, object?>)agent["output"]!;
        output["instructions"] = " ";
        var capability = (Dictionary<string, object?>)((object[])agent["capabilities"]!)[0];
        capability.Remove("instructions");

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("output.instructions is required and must be non-blank", message);
        Assert.Contains("capabilities[0].instructions is required and must be non-blank", message);
    }

    [Fact]
    public void RejectsMissingDuplicateAndNonAgentSkillDirectories()
    {
        var value = ContractObject();
        var nodes = (object[])value["nodes"]!;
        var agent = (Dictionary<string, object?>)nodes[0];
        var terminal = (Dictionary<string, object?>)nodes[1];
        agent["skillDirectories"] = new[] { "/skills/one", "/skills/one", " " };
        terminal["skillDirectories"] = Array.Empty<string>();

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("skillDirectories[1] duplicates '/skills/one'", message);
        Assert.Contains("skillDirectories[2] must be non-blank", message);
        Assert.Contains("nodes[1].skillDirectories is forbidden", message);
    }

    private static string ValidContract() => JsonSerializer.Serialize(ContractObject());

    private static Dictionary<string, object?> ContractObject() =>
        new()
        {
            ["contractVersion"] = 6,
            ["name"] = "test",
            ["start"] = "agent",
            ["initialState"] = "{}",
            ["persist"] = false,
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
                        ["instructions"] = "Return a result.",
                        ["jsonSchema"] = "{\"type\":\"object\"}",
                        ["validateCallback"] = "agent.output.validate",
                        ["applyCallback"] = "agent.output.apply",
                        ["valueType"] = "result",
                    },
                    ["capabilities"] = new object[]
                    {
                        Capability("first", "agent.first.validate"),
                        Capability("second", "agent.second.validate"),
                    },
                    ["skillDirectories"] = new[] { "/skills/meridian" },
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

    private static Dictionary<string, object?> InteractionContractObject() =>
        new()
        {
            ["contractVersion"] = 6,
            ["name"] = "interaction-test",
            ["start"] = "review",
            ["initialState"] = "{}",
            ["persist"] = false,
            ["nodes"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "review",
                    ["kind"] = "interaction",
                    ["requestCallback"] = "c0",
                    ["applyCallback"] = "c1",
                },
                new Dictionary<string, object?>
                {
                    ["id"] = "done",
                    ["kind"] = "completion",
                    ["summaryCallback"] = "c3",
                },
            },
            ["routes"] = new[]
            {
                new
                {
                    source = "review",
                    target = "done",
                    label = "reviewed",
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
            ["instructions"] = $"Invoke {name}.",
            ["jsonSchema"] = "{\"type\":\"object\"}",
            ["validateCallback"] = validate,
            ["applyCallback"] = $"agent.{name}.apply",
            ["summaryCallback"] = $"agent.{name}.summary",
            ["valueType"] = $"capability.{name}",
        };
}
