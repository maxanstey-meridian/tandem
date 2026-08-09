using System.Diagnostics;
using System.Text.Json;

namespace Tandem.NodeApiSpike;

internal static class RegisteredParticipantFactory
{
    public static async Task<RegisteredParticipant> CreateAsync(
        RegisteredNodeContract node,
        CallbackDispatcher callbacks
    ) =>
        node.Kind switch
        {
            "stage" => RegisteredParticipant.ForStage(
                node,
                new JavaScriptStage(
                    node.Id!,
                    state => callbacks.InvokeAsync(node.RunCallback!, state, "")
                )
            ),
            "interaction" => CreateInteraction(node, callbacks),
            "agent" => await CreateAgentAsync(node, callbacks),
            "completion" => RegisteredParticipant.ForNode(
                node,
                PipelineNodes.Complete(
                    new JavaScriptCompletion(
                        node.Id!,
                        state => callbacks.Invoke(node.SummaryCallback!, state, "")
                    )
                )
            ),
            "failure" => RegisteredParticipant.ForNode(
                node,
                PipelineNodes.Failed(
                    new JavaScriptFailure(
                        node.Id!,
                        state => callbacks.Invoke(node.SummaryCallback!, state, "")
                    )
                )
            ),
            _ => throw new UnreachableException(),
        };

    private static RegisteredParticipant CreateInteraction(
        RegisteredNodeContract node,
        CallbackDispatcher callbacks
    )
    {
        var interaction = PipelineNodes.WaitFor<JavaScriptState, string, string>(
            node.Id!,
            state => callbacks.Invoke(node.RequestCallback!, state.Json, ""),
            (state, response) => new(callbacks.Invoke(node.ApplyCallback!, state.Json, response))
        );
        return RegisteredParticipant.ForInteraction(node, interaction);
    }

    private static async Task<RegisteredParticipant> CreateAgentAsync(
        RegisteredNodeContract node,
        CallbackDispatcher callbacks
    )
    {
        var builder = Agent
            .Create<JavaScriptState>(
                node.Id!,
                node.Instructions!,
                await OpenAiCompatibleChatClients.CreateAsync(node.Client!)
            )
            .WithMessage(state => callbacks.Invoke(node.MessageCallback!, state.Json, ""));
        if (node.Output is { } outputContract)
        {
            using var schema = JsonDocument.Parse(outputContract.JsonSchema!);
            builder.WithJsonOutput(
                new AgentJsonOutputDefinition<JavaScriptState>(
                    schema.RootElement.Clone(),
                    "Return the requested structured value.",
                    candidate =>
                        Problems(
                            callbacks.Invoke(
                                outputContract.ValidateCallback!,
                                "",
                                candidate.GetRawText()
                            )
                        ),
                    ContractName: outputContract.ContractName!
                ),
                (state, candidate) =>
                    new(
                        callbacks.Invoke(
                            outputContract.ApplyCallback!,
                            state.Json,
                            candidate.GetRawText()
                        )
                    )
            );
        }
        foreach (var capabilityContract in node.Capabilities ?? [])
        {
            using var schema = JsonDocument.Parse(capabilityContract.JsonSchema!);
            builder.WithCapability(
                AgentCapabilities.CreateJson(
                    new AgentJsonCapabilityDefinition<JavaScriptState>(
                        capabilityContract.Name!,
                        $"Invoke {capabilityContract.Name}.",
                        schema.RootElement.Clone(),
                        request =>
                            Problems(
                                callbacks.Invoke(
                                    capabilityContract.ValidateCallback!,
                                    "",
                                    request.GetRawText()
                                )
                            ),
                        null,
                        request =>
                            callbacks.Invoke(
                                capabilityContract.SummaryCallback!,
                                "",
                                request.GetRawText()
                            ),
                        capabilityContract.ContractName!
                    ),
                    (state, request) =>
                        new(
                            callbacks.Invoke(
                                capabilityContract.ApplyCallback!,
                                state.Json,
                                request.GetRawText()
                            )
                        )
                )
            );
        }
        ApplyAgentPolicies(builder, node);
        var agent = builder.Build();
        return RegisteredParticipant.ForStandard(node, agent, agent.Success, agent.Failed);
    }

    private static void ApplyAgentPolicies(
        AgentBuilder<JavaScriptState> builder,
        RegisteredNodeContract node
    )
    {
        if (node.ContinueSession)
        {
            builder.ContinueSession();
        }

        if (node.TimeoutMilliseconds is { } timeout)
        {
            builder.WithTimeout(TimeSpan.FromMilliseconds(timeout));
        }
    }

    private static IReadOnlyList<AgentJsonValidationProblem> Problems(string problems)
    {
        if (string.IsNullOrWhiteSpace(problems))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<AgentJsonValidationProblem[]>(
                    problems,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                ) ?? [];
        }
        catch (JsonException)
        {
            return [new AgentJsonValidationProblem("$", problems)];
        }
    }
}

internal sealed class RegisteredParticipant
{
    private RegisteredParticipant(RegisteredNodeContract contract) => Contract = contract;

    public RegisteredNodeContract Contract { get; }
    public IGeneratedPipelineStep<JavaScriptState, GeneratedStepCompletion>? Stage
    {
        get;
        private init;
    }
    public IStandardOutcomePipelineStep<JavaScriptState>? Standard { get; private init; }
    public PipelineOutcomeSelector<JavaScriptState>? Success { get; private init; }
    public PipelineOutcomeSelector<JavaScriptState>? Failed { get; private init; }
    public PipelineInteraction<JavaScriptState, string, string>? Interaction { get; private init; }
    public IPipelineNode<JavaScriptState> Node =>
        NodeOverride
        ?? (IPipelineNode<JavaScriptState>?)Stage
        ?? Standard
        ?? throw new UnreachableException();
    private IPipelineNode<JavaScriptState>? NodeOverride { get; init; }

    public static RegisteredParticipant ForStage(
        RegisteredNodeContract c,
        IGeneratedPipelineStep<JavaScriptState, GeneratedStepCompletion> s
    ) => new(c) { Stage = s };

    public static RegisteredParticipant ForStandard(
        RegisteredNodeContract c,
        IStandardOutcomePipelineStep<JavaScriptState> s,
        PipelineOutcomeSelector<JavaScriptState> success,
        PipelineOutcomeSelector<JavaScriptState> failed
    ) =>
        new(c)
        {
            Standard = s,
            Success = success,
            Failed = failed,
        };

    public static RegisteredParticipant ForInteraction(
        RegisteredNodeContract c,
        PipelineInteraction<JavaScriptState, string, string> i
    ) => new(c) { Interaction = i };

    public static RegisteredParticipant ForNode(
        RegisteredNodeContract c,
        IPipelineNode<JavaScriptState> n
    ) => new(c) { NodeOverride = n };
}
