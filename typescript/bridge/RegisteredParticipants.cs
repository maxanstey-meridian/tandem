using System.Diagnostics;
using System.Text.Json;

namespace Tandem.NodeApiSpike;

internal static class RegisteredParticipantFactory
{
    public static async Task<RegisteredParticipant> CreateAsync(
        RegisteredNodeContract node,
        CallbackDispatcher callbacks,
        CancellationToken cancellationToken
    ) =>
        node.Kind switch
        {
            "stage" => RegisteredParticipant.ForStage(
                node,
                PipelineNodes.Stage<JavaScriptState>(
                    node.Id!,
                    async (state, token) =>
                        new(await callbacks.InvokeAsync(node.RunCallback!, state.Json, "", token))
                )
            ),
            "interaction" => CreateInteraction(node, callbacks),
            "agent" => await CreateAgentAsync(node, callbacks, cancellationToken),
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
        CallbackDispatcher callbacks,
        CancellationToken cancellationToken
    )
    {
        var builder = Agent
            .Create<JavaScriptState>(
                node.Id!,
                node.Instructions!,
                await OpenAiCompatibleChatClients.CreateAsync(node.Client!, cancellationToken)
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
                        ParseValidationProblems(
                            callbacks.Invoke(
                                outputContract.ValidateCallback!,
                                "",
                                candidate.GetRawText()
                            )
                        ),
                    ValueType: outputContract.ValueType!
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
                            ParseValidationProblems(
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
                        capabilityContract.ValueType!
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

    internal static IReadOnlyList<AgentJsonValidationProblem> ParseValidationProblems(
        string problems
    )
    {
        if (string.IsNullOrWhiteSpace(problems))
        {
            return [];
        }

        return JsonSerializer.Deserialize<AgentJsonValidationProblem[]>(
                problems,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            ) ?? [];
    }
}

internal abstract record RegisteredParticipant(RegisteredNodeContract Contract)
{
    public static RegisteredParticipant ForStage(
        RegisteredNodeContract c,
        IGeneratedPipelineStep<JavaScriptState, GeneratedStepCompletion> s
    ) => new RegisteredStage(c, s);

    public static RegisteredParticipant ForStandard(
        RegisteredNodeContract c,
        IStandardOutcomePipelineStep<JavaScriptState> s,
        PipelineOutcomeSelector<JavaScriptState> success,
        PipelineOutcomeSelector<JavaScriptState> failed
    ) => new RegisteredStandard(c, s, success, failed);

    public static RegisteredParticipant ForInteraction(
        RegisteredNodeContract c,
        PipelineInteraction<JavaScriptState, string, string> i
    ) => new RegisteredInteraction(c, i);

    public static RegisteredParticipant ForNode(
        RegisteredNodeContract c,
        IPipelineNode<JavaScriptState> n
    ) => new RegisteredTerminal(c, n);
}

internal sealed record RegisteredStage(
    RegisteredNodeContract Contract,
    IGeneratedPipelineStep<JavaScriptState, GeneratedStepCompletion> Stage
) : RegisteredParticipant(Contract);

internal sealed record RegisteredStandard(
    RegisteredNodeContract Contract,
    IStandardOutcomePipelineStep<JavaScriptState> Standard,
    PipelineOutcomeSelector<JavaScriptState> Success,
    PipelineOutcomeSelector<JavaScriptState> Failed
) : RegisteredParticipant(Contract);

internal sealed record RegisteredInteraction(
    RegisteredNodeContract Contract,
    PipelineInteraction<JavaScriptState, string, string> Interaction
) : RegisteredParticipant(Contract);

internal sealed record RegisteredTerminal(
    RegisteredNodeContract Contract,
    IPipelineNode<JavaScriptState> Terminal
) : RegisteredParticipant(Contract);
