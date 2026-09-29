using System.Text.Json;
using Xunit;

namespace Tandem.NodeApiSpike;

public sealed class RegistrationContractValidatorTests
{
    [Fact]
    public void AcceptsVersionTenAgentWithOutputCapabilitiesSkillsAndModelRequestControls()
    {
        var value = ContractObject();
        ((Dictionary<string, object?>)((object[])value["nodes"]!)[0])["skillDirectories"] = new[]
        {
            "/skills/meridian",
        };

        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        var agent = Assert.IsType<AgentNodeContract>(contract.Nodes[0]);
        Assert.Equal(10, contract.ContractVersion);
        Assert.Equal(2, agent.Capabilities.Length);
        Assert.Single(agent.SkillDirectories);
        Assert.NotNull(agent.Output);
        Assert.Equal(0, agent.Temperature);
        Assert.Equal(4096, agent.MaxOutputTokens);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("workspace")]
    [InlineData("parallel")]
    [InlineData("interaction")]
    public void BuildsValidContractsThroughTandemBuilders(string fixture)
    {
        var value = fixture switch
        {
            "parallel" => ParallelContractObject(),
            "interaction" => InteractionContractObject(),
            _ => ContractObject(),
        };
        if (fixture == "workspace")
            ((Dictionary<string, object?>)((object[])value["nodes"]!)[0])["workspace"] =
                WorkspaceContract();
        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );

        var (pipeline, _) = NodePipelineBridge.BuildGraph(contract, Callbacks());

        Assert.NotNull(pipeline);
    }

    [Theory]
    [InlineData("unknown-kind", "'script'")]
    [InlineData("missing-kind", "registration JSON is invalid")]
    [InlineData("null-node", "nodes[1] must not be null")]
    [InlineData("null-capability", "nodes[0].capabilities[0] must not be null")]
    public void RejectsUnknownKindsAndNullEntries(string scenario, string expected)
    {
        var value = ContractObject();
        var nodes = (object?[])value["nodes"]!;
        var agent = (Dictionary<string, object?>)nodes[0]!;
        var terminal = (Dictionary<string, object?>)nodes[1]!;
        switch (scenario)
        {
            case "unknown-kind":
                terminal["kind"] = "script";
                break;
            case "missing-kind":
                terminal.Remove("kind");
                break;
            case "null-node":
                nodes[1] = null;
                break;
            case "null-capability":
                agent["capabilities"] = new object?[] { null };
                break;
        }

        Assert.Contains(expected, ContractError(value));
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
            Assert.IsType<AgentNodeContract>(contract.Nodes[0]).Workspace!.ToolGroups[0].Tools
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

        Assert.Equal(2, Assert.IsType<ParallelNodeContract>(contract.Nodes[0]).Branches.Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void AcceptsParallelMax(int max)
    {
        var value = ParallelContractObject();
        var node = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        node["max"] = max;
        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );
        Assert.Equal(max, Assert.IsType<ParallelNodeContract>(contract.Nodes[0]).Max);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RejectsNonPositiveParallelMax(int max)
    {
        var value = ParallelContractObject();
        var node = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        node["max"] = max;

        Assert.Contains("nodes[0]: Parallel max must be positive", BuildError(value));
    }

    [Fact]
    public void RejectsMaxOnOrdinaryParticipant()
    {
        var value = ParallelContractObject();
        var node = (Dictionary<string, object?>)((object[])value["nodes"]!)[1];
        node["max"] = 5;

        Assert.Contains("'max' could not be mapped", ContractError(value));
    }

    [Theory]
    [InlineData("one-branch", true, "A parallel group requires at least two branches")]
    [InlineData("duplicate-branch", true, "Parallel branch IDs must be unique")]
    [InlineData("duplicate-participant", true, "'first'")]
    [InlineData("nested-terminal", false, "kind 'completion' is unsupported in a parallel branch")]
    [InlineData("duplicate-callback", false, "duplicates callback reference 'merge'")]
    [InlineData("nested-route-target", false, "target references unknown node 'first'")]
    [InlineData("nested-persistence", false, "ledgerPath is required when persistence is enabled")]
    [InlineData("parallel-field-on-stage", false, "'branches' could not be mapped")]
    public void RejectsInvalidParallelContracts(string scenario, bool coreRule, string expected)
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
            case "nested-terminal":
                firstParticipant["kind"] = "completion";
                firstParticipant.Remove("runCallback");
                firstParticipant["summaryCallback"] = "first.summary";
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

        Assert.Contains(expected, coreRule ? BuildError(value) : ContractError(value));
    }

    [Fact]
    public void RejectsDuplicateCapabilitiesAndMissingAuthoritativeValidation()
    {
        var value = ContractObject();
        var nodes = (object[])value["nodes"]!;
        var agent = (Dictionary<string, object?>)nodes[0];
        agent["capabilities"] = new object[]
        {
            Capability("same", validate: "agent.first.validate"),
            Capability("same", validate: "agent.second.validate"),
        };
        Assert.Contains("duplicates capability 'same'", ContractError(value));

        agent["capabilities"] = new object[] { Capability("first", validate: null) };
        Assert.Contains("validateCallback", ContractError(value));
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
        Assert.Contains("contractVersion must be 10", ContractError(value));

        value["contractVersion"] = 10;
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        var output = (Dictionary<string, object?>)agent["output"]!;
        output["jsonSchema"] = "{\"type\":\"array\"}";
        Assert.Contains(
            "nodes[0]: Output JSON schema must declare an object root with type 'object'",
            BuildError(value)
        );
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

        var workspace = Assert.IsType<AgentNodeContract>(contract.Nodes[0]).Workspace!;
        Assert.Equal("workspace.path", workspace.PathCallback);
        Assert.Equal("workspace.commands", workspace.CommandsCallback);
        Assert.Null(workspace.InterceptCallback);
        Assert.Equal(2, workspace.ToolGroups.Length);
        Assert.True(workspace.ToolGroups[0].IncludeCommands);
        Assert.Equal("workspace.can-mutate", workspace.ToolGroups[1].WhenCallback);
    }

    [Theory]
    [InlineData("unknown", "Unknown agent workspace tool 'unknown'")]
    [InlineData(
        "duplicate",
        "A workspace cannot select the same effective tool in more than one group"
    )]
    [InlineData(
        "commands-twice",
        "A workspace cannot select the same effective tool in more than one group"
    )]
    [InlineData("empty", "A tool group must select at least one tool")]
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

        var message = BuildError(value);
        Assert.True(message.Contains($"nodes[0]: {expected}"), message);
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

        Assert.Equal(
            presentation is null ? null : RegisteredPresentation.Terminal,
            contract.Presentation
        );
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

        Assert.Contains("$.presentation", ContractError(value));
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

        Assert.Contains(
            "routes from 'agent' for outcome 'success' contain more than one unconditional route",
            ContractError(value)
        );
    }

    [Fact]
    public void SurfacesBuilderRejectionOfMultipleUnconditionalStepRoutes()
    {
        var value = InteractionContractObject();
        value["routes"] = new object[]
        {
            new
            {
                source = "review",
                target = "done",
                label = "first",
            },
            new
            {
                source = "review",
                target = "done",
                label = "second",
            },
        };

        Assert.Contains("routes[1]: Step 'review", BuildError(value));
        Assert.Contains("cannot declare more than one unconditional route", BuildError(value));
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
        Assert.Contains("handleCallback must be non-blank", message);
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
        Assert.Contains("nodes[0]: The value cannot be an empty string", BuildError(value));

        output["instructions"] = "Return a result.";
        var capability = (Dictionary<string, object?>)((object[])agent["capabilities"]!)[0];
        capability.Remove("instructions");
        Assert.Contains("instructions", ContractError(value));
    }

    [Fact]
    public void RejectsMissingDuplicateAndNonAgentSkillDirectories()
    {
        var value = ContractObject();
        var nodes = (object[])value["nodes"]!;
        var agent = (Dictionary<string, object?>)nodes[0];
        var terminal = (Dictionary<string, object?>)nodes[1];
        var skill = Directory.CreateTempSubdirectory("tandem-skill-");
        try
        {
            File.WriteAllText(Path.Combine(skill.FullName, "SKILL.md"), "# Skill");
            agent["skillDirectories"] = new[] { skill.FullName, skill.FullName };
            Assert.Contains("more than once", BuildError(value));

            agent["skillDirectories"] = new[] { " " };
            Assert.Contains("nodes[0]: The value cannot be an empty string", BuildError(value));
        }
        finally
        {
            skill.Delete(recursive: true);
        }

        agent["skillDirectories"] = Array.Empty<string>();
        terminal["skillDirectories"] = Array.Empty<string>();
        Assert.Contains("'skillDirectories' could not be mapped", ContractError(value));
    }

    [Fact]
    public void RejectsInvalidAndNonAgentModelRequestControls()
    {
        var value = ContractObject();
        var nodes = (object[])value["nodes"]!;
        var agent = (Dictionary<string, object?>)nodes[0];
        var terminal = (Dictionary<string, object?>)nodes[1];
        agent["temperature"] = 2.1;
        Assert.Contains("(Parameter 'temperature')", BuildError(value));

        agent["temperature"] = 0;
        agent["maxOutputTokens"] = 0;
        Assert.Contains("(Parameter 'maxOutputTokens')", BuildError(value));

        agent["maxOutputTokens"] = 1;
        terminal["temperature"] = 0;
        Assert.Contains("'temperature' could not be mapped", ContractError(value));
    }

    [Theory]
    [InlineData("timeout", "(Parameter 'timeout')")]
    [InlineData("checkpoint-window", "(Parameter 'MaxOutputTokens')")]
    [InlineData("checkpoint-percent", "(Parameter 'CheckpointAtPercent')")]
    public void SurfacesBuilderRangeRules(string scenario, string expected)
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        var checkpoint = new Dictionary<string, object?>
        {
            ["contextWindowTokens"] = 100,
            ["maxOutputTokens"] = 20,
            ["checkpointAtPercent"] = 80,
            ["capabilityName"] = "first",
            ["instructions"] = "Checkpoint.",
            ["messageCallback"] = "checkpoint.message",
        };
        switch (scenario)
        {
            case "timeout":
                agent["timeoutMilliseconds"] = (double)uint.MaxValue;
                break;
            case "checkpoint-window":
                checkpoint["maxOutputTokens"] = 100;
                agent["checkpoint"] = checkpoint;
                break;
            case "checkpoint-percent":
                checkpoint["checkpointAtPercent"] = 100;
                agent["checkpoint"] = checkpoint;
                break;
        }

        Assert.Contains("nodes[0]: Specified argument was out of the range", BuildError(value));
        Assert.Contains(expected, BuildError(value));
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

        Assert.Equal(
            disableCompaction,
            Assert.IsType<AgentNodeContract>(contract.Nodes[0]).Checkpoint!.DisableCompaction
        );
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

        Assert.False(
            Assert.IsType<AgentNodeContract>(contract.Nodes[0]).Checkpoint!.DisableCompaction
        );
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

        Assert.Contains("'checkpoint' could not be mapped", message);
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

        Assert.Equal(
            AgentReasoningEffort.None,
            Assert.IsType<AgentNodeContract>(contract.Nodes[0]).Reasoning!.Effort
        );
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

        Assert.Equal(
            1024,
            Assert.IsType<AgentNodeContract>(contract.Nodes[0]).Reasoning!.MaxTokens
        );
    }

    [Theory]
    [InlineData(1023)]
    [InlineData(0)]
    public void RejectsReasoningTokenBudgetBelowOpenRouterMinimum(int maxTokens)
    {
        var value = ContractObject();
        var agent = (Dictionary<string, object?>)((object[])value["nodes"]!)[0];
        var client = Client("http://127.0.0.1:10531/v1", null);
        client["wireApi"] = "completions";
        agent["client"] = client;
        agent["reasoning"] = new Dictionary<string, object?> { ["maxTokens"] = maxTokens };

        Assert.Contains("(Parameter 'reasoningMaxTokens')", BuildError(value));
    }

    private static string ContractError(object value) =>
        Assert
            .Throws<InvalidOperationException>(() =>
                RegistrationContractValidator.ParseAndValidate(JsonSerializer.Serialize(value))
            )
            .Message;

    private static string BuildError(object value)
    {
        var contract = RegistrationContractValidator.ParseAndValidate(
            JsonSerializer.Serialize(value)
        );
        return Assert
            .Throws<InvalidOperationException>(() =>
                NodePipelineBridge.BuildGraph(contract, Callbacks())
            )
            .Message;
    }

    private static CallbackDispatcher Callbacks() =>
        new(
            new SynchronizationContext(),
            (_, _, _) => "",
            (_, _, _, _) => Task.FromResult(""),
            CancellationToken.None
        );

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
                    ["skillDirectories"] = Array.Empty<string>(),
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
