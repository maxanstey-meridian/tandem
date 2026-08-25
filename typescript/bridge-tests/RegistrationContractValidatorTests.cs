using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class RegistrationContractValidatorTests
{
    [Fact]
    public void AcceptsVersionTenAgentWithOutputCapabilitiesSkillsAndModelRequestControls()
    {
        var contract = RegistrationContractValidator.ParseAndValidate(ValidContract());

        Assert.Equal(10, contract.ContractVersion);
        Assert.Equal(2, contract.Nodes![0].Capabilities!.Length);
        Assert.Single(contract.Nodes[0].SkillDirectories!);
        Assert.NotNull(contract.Nodes[0].Output);
        Assert.Equal(0, contract.Nodes[0].Temperature);
        Assert.Equal(4096, contract.Nodes[0].MaxOutputTokens);
    }

    [Fact]
    public void AcceptsTavilyWebToolNamesInWorkspaceGroups()
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        agent["workspace"] = new
        {
            pathCallback = "workspace.path",
            commandsCallback = "workspace.commands",
            toolGroups = new[]
            {
                new { tools = new[] { "web_search", "web_fetch" }, includeCommands = false },
            },
        };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.Equal(
            new[] { "web_search", "web_fetch" },
            contract.Nodes![0].Workspace!.ToolGroups![0].Tools
        );
    }

    [Fact]
    public void AcceptsVersionTenParallelGroupWithNestedStages()
    {
        var value = new Dictionary<string, object?>
        {
            ["contractVersion"] = 10,
            ["name"] = "parallel",
            ["start"] = "parallel",
            ["initialState"] = "{}",
            ["persist"] = false,
            ["nodes"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "parallel",
                    ["kind"] = "parallel",
                    ["mergeCallback"] = "merge",
                    ["branches"] = new object[]
                    {
                        new
                        {
                            id = "one",
                            participant = new
                            {
                                id = "first",
                                kind = "stage",
                                runCallback = "first.run",
                            },
                        },
                        new
                        {
                            id = "two",
                            participant = new
                            {
                                id = "second",
                                kind = "stage",
                                runCallback = "second.run",
                            },
                        },
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
                    source = "parallel",
                    target = "done",
                    label = "done",
                    outcome = "success",
                },
            },
            ["outputs"] = new[] { "done" },
        };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.Equal("parallel", contract.Nodes![0].Kind);
        Assert.Equal(2, contract.Nodes[0].Branches!.Length);
    }

    [Theory]
    [InlineData("one-branch", "branches must contain at least two branches")]
    [InlineData("duplicate-branch", "duplicates branch ID 'one'")]
    [InlineData("duplicate-participant", "duplicates participant ID 'first'")]
    [InlineData("nested-parallel", "kind 'parallel' is unsupported in a parallel branch")]
    [InlineData("duplicate-callback", "duplicates callback reference 'merge'")]
    [InlineData("nested-route-target", "target references unknown node 'first'")]
    [InlineData("nested-persistence", "ledgerPath is required when persistence is enabled")]
    [InlineData("parallel-field-on-stage", "branches is forbidden")]
    public void RejectsInvalidParallelContracts(string scenario, string expected)
    {
        var value = ParallelContractObject();
        var nodes = (object[])value["nodes"]!;
        var parallel = (Dictionary<string, object?>)nodes[0];
        var branches = (object[])parallel["branches"]!;
        var firstBranch = (Dictionary<string, object?>)branches[0];
        var secondBranch = (Dictionary<string, object?>)branches[1];
        var firstParticipant = (Dictionary<string, object?>)firstBranch["participant"]!;
        var secondParticipant = (Dictionary<string, object?>)secondBranch["participant"]!;

        switch (scenario)
        {
            case "one-branch":
                parallel["branches"] = new[] { firstBranch };
                break;
            case "duplicate-branch":
                secondBranch["id"] = "one";
                break;
            case "duplicate-participant":
                secondParticipant["id"] = "first";
                break;
            case "nested-parallel":
                firstParticipant["kind"] = "parallel";
                firstParticipant.Remove("runCallback");
                break;
            case "duplicate-callback":
                firstParticipant["runCallback"] = "merge";
                break;
            case "nested-route-target":
                ((Dictionary<string, object?>)((object[])value["routes"]!)[0])["target"] = "first";
                break;
            case "nested-persistence":
                firstParticipant["persist"] = true;
                break;
            case "parallel-field-on-stage":
                firstParticipant["branches"] = Array.Empty<object>();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains(expected, message);
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

        Assert.Contains("contractVersion must be 10", message);
        Assert.Contains("object root with type 'object'", message);
    }

    [Fact]
    public void AcceptsVersionTenWorkspaceCallbacksAndConditionalTools()
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        agent["workspace"] = WorkspaceContract();

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        var workspace = contract.Nodes![0].Workspace!;
        Assert.Equal("workspace.path", workspace.PathCallback);
        Assert.Equal("workspace.commands", workspace.CommandsCallback);
        Assert.Null(workspace.InterceptCallback);
        Assert.Equal(2, workspace.ToolGroups!.Length);
        Assert.True(workspace.ToolGroups[0].IncludeCommands);
        Assert.Equal("workspace.can-mutate", workspace.ToolGroups[1].WhenCallback);
    }

    [Theory]
    [InlineData("unknown", "unknown tool")]
    [InlineData("duplicate", "in more than one group")]
    [InlineData("commands-twice", "workspace commands more than once")]
    [InlineData("empty", "must select at least one tool")]
    public void RejectsMalformedVersionTenWorkspacePolicies(string scenario, string expected)
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        var workspace = WorkspaceContract();
        var groups = (object[])workspace["toolGroups"]!;
        var first = (Dictionary<string, object?>)groups[0];
        var second = (Dictionary<string, object?>)groups[1];
        switch (scenario)
        {
            case "unknown":
                first["tools"] = new[] { "unknown" };
                break;
            case "duplicate":
                second["tools"] = new[] { "read_file" };
                break;
            case "commands-twice":
                second["includeCommands"] = true;
                break;
            case "empty":
                first["tools"] = Array.Empty<string>();
                first["includeCommands"] = false;
                break;
        }
        agent["workspace"] = workspace;

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains(expected, message);
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
    public void AcceptsExactTerminalTruncatedToolNames()
    {
        var value = ContractObject();
        value["presentation"] = "terminal";
        value["terminal"] = new
        {
            truncatedToolNames = new[] { "write_checkpoint", "file_access_write" },
        };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.Equal(
            ["write_checkpoint", "file_access_write"],
            contract.Terminal!.TruncatedToolNames!
        );
    }

    [Fact]
    public void RejectsTerminalOptionsWithoutTerminalPresentationOrWithInvalidNames()
    {
        var withoutPresentation = ContractObject();
        withoutPresentation["terminal"] = new { truncatedToolNames = new[] { "write_checkpoint" } };
        var presentationMessage = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(
                    JsonSerializer.Serialize(withoutPresentation)
                )
            )
            .Message;
        Assert.Contains("terminal options require terminal presentation", presentationMessage);

        var invalidNames = ContractObject();
        invalidNames["presentation"] = "terminal";
        invalidNames["terminal"] = new
        {
            truncatedToolNames = new[] { "write_checkpoint", " ", "write_checkpoint" },
        };
        var namesMessage = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(
                    JsonSerializer.Serialize(invalidNames)
                )
            )
            .Message;
        Assert.Contains("terminal.truncatedToolNames[1] must be non-blank", namesMessage);
        Assert.Contains(
            "terminal.truncatedToolNames[2] duplicates 'write_checkpoint'",
            namesMessage
        );
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

    [Fact]
    public void RejectsInvalidAndNonAgentModelRequestControls()
    {
        var value = ContractObject();
        var nodes = (object[])value["nodes"]!;
        var agent = (Dictionary<string, object?>)nodes[0];
        var terminal = (Dictionary<string, object?>)nodes[1];
        agent["temperature"] = 2.1;
        agent["maxOutputTokens"] = 0;
        terminal["temperature"] = 0;
        terminal["maxOutputTokens"] = 1;

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("nodes[0].temperature must be a finite number between 0 and 2", message);
        Assert.Contains("nodes[0].maxOutputTokens must be a positive integer", message);
        Assert.Contains("nodes[1].temperature is forbidden", message);
        Assert.Contains("nodes[1].maxOutputTokens is forbidden", message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesCheckpointDisableCompaction(bool disableCompaction)
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        agent["checkpoint"] = new Dictionary<string, object?>
        {
            ["contextWindowTokens"] = 100,
            ["maxOutputTokens"] = 20,
            ["checkpointAtPercent"] = 80,
            ["capabilityName"] = "first",
            ["instructions"] = "Checkpoint.",
            ["messageCallback"] = "checkpoint.message",
            ["resetSession"] = true,
            ["disableCompaction"] = disableCompaction,
        };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.Equal(disableCompaction, contract.Nodes![0].Checkpoint!.DisableCompaction);
    }

    [Fact]
    public void CheckpointDisableCompaction_OmissionDefaultsToFalse()
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        agent["checkpoint"] = new Dictionary<string, object?>
        {
            ["contextWindowTokens"] = 100,
            ["maxOutputTokens"] = 20,
            ["checkpointAtPercent"] = 80,
            ["capabilityName"] = "first",
            ["instructions"] = "Checkpoint.",
            ["messageCallback"] = "checkpoint.message",
            ["resetSession"] = true,
        };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.False(contract.Nodes![0].Checkpoint!.DisableCompaction);
    }

    [Fact]
    public void RejectsCheckpointOnNonAgentNode()
    {
        var value = ContractObject();
        var terminal = (Dictionary<string, object?>)((object[])value["nodes"]!)[1];
        terminal["checkpoint"] = new Dictionary<string, object?>
        {
            ["contextWindowTokens"] = 100,
            ["maxOutputTokens"] = 20,
            ["checkpointAtPercent"] = 80,
            ["capabilityName"] = "first",
            ["instructions"] = "Checkpoint.",
            ["messageCallback"] = "checkpoint.message",
            ["resetSession"] = true,
        };

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("nodes[1].checkpoint is forbidden", message);
    }

    [Fact]
    public void AcceptsExplicitReasoningDisable()
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        agent["reasoning"] = new Dictionary<string, object?> { ["effort"] = "none" };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.Equal("none", contract.Nodes![0].Reasoning!.Effort);
    }

    [Fact]
    public void AcceptsReasoningTokenBudget()
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        var client = Client("https://openrouter.ai/api/v1", "TANDEM_TEST_OPENROUTER_KEY");
        client["wireApi"] = "completions";
        agent["client"] = client;
        agent["reasoning"] = new Dictionary<string, object?> { ["maxTokens"] = 1024 };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        Assert.Equal(1024, contract.Nodes![0].Reasoning!.MaxTokens);
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(0)]
    public void RejectsReasoningTokenBudgetBelowOpenRouterMinimum(int maxTokens)
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        agent["reasoning"] = new Dictionary<string, object?> { ["maxTokens"] = maxTokens };

        var message = Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

        Assert.Contains("reasoning.maxTokens must be at least 1024", message);
    }

    private static string ValidContract() => JsonSerializer.Serialize(ContractObject());

    private static Dictionary<string, object?> ParallelContractObject() =>
        new()
        {
            ["contractVersion"] = 10,
            ["name"] = "parallel",
            ["start"] = "parallel",
            ["initialState"] = "{}",
            ["persist"] = false,
            ["nodes"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "parallel",
                    ["kind"] = "parallel",
                    ["mergeCallback"] = "merge",
                    ["branches"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["id"] = "one",
                            ["participant"] = new Dictionary<string, object?>
                            {
                                ["id"] = "first",
                                ["kind"] = "stage",
                                ["runCallback"] = "first.run",
                            },
                        },
                        new Dictionary<string, object?>
                        {
                            ["id"] = "two",
                            ["participant"] = new Dictionary<string, object?>
                            {
                                ["id"] = "second",
                                ["kind"] = "stage",
                                ["runCallback"] = "second.run",
                            },
                        },
                    },
                },
                new Dictionary<string, object?>
                {
                    ["id"] = "done",
                    ["kind"] = "completion",
                    ["summaryCallback"] = "done.summary",
                },
            },
            ["routes"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["source"] = "parallel",
                    ["target"] = "done",
                    ["label"] = "done",
                    ["outcome"] = "success",
                },
            },
            ["outputs"] = new[] { "done" },
        };

    private static Dictionary<string, object?> ContractObject() =>
        new()
        {
            ["contractVersion"] = 10,
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
                    ["temperature"] = 0,
                    ["maxOutputTokens"] = 4096,
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
            ["contractVersion"] = 10,
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

    private static Dictionary<string, object?> WorkspaceContract() =>
        new()
        {
            ["pathCallback"] = "workspace.path",
            ["commandsCallback"] = "workspace.commands",
            ["toolGroups"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tools"] = new[] { "read_file", "git:ro" },
                    ["includeCommands"] = true,
                },
                new Dictionary<string, object?>
                {
                    ["tools"] = new[] { "write_file" },
                    ["includeCommands"] = false,
                    ["whenCallback"] = "workspace.can-mutate",
                },
            },
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
