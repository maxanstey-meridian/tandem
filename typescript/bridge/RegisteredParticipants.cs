using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;

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
            "parallel" => await CreateParallelAsync(node, callbacks, cancellationToken),
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

    private static async Task<RegisteredParticipant> CreateParallelAsync(
        RegisteredNodeContract node,
        CallbackDispatcher callbacks,
        CancellationToken cancellationToken
    )
    {
        var owned = new List<RegisteredParticipant>();
        var branches = new List<PipelineBranch<JavaScriptState>>();
        foreach (var branch in node.Branches!)
        {
            var participant = await CreateAsync(branch.Participant!, callbacks, cancellationToken);
            owned.Add(participant);
            branches.Add(
                participant switch
                {
                    RegisteredStage stage => PipelineBranch.Create(branch.Id!, stage.Stage),
                    RegisteredStandard standard => PipelineBranch.Create(
                        branch.Id!,
                        standard.Standard
                    ),
                    _ => throw new UnreachableException(),
                }
            );
        }
        var parallel = PipelineNodes.Parallel(
            node.Id!,
            state => new JavaScriptState(state.Json),
            branches,
            results =>
            {
                var states = results.BranchIds.ToDictionary(
                    id => id,
                    id => ParseElement(results.State(id).Json),
                    StringComparer.Ordinal
                );
                return new JavaScriptState(
                    callbacks.Invoke(
                        node.MergeCallback!,
                        results.Baseline.Json,
                        JsonSerializer.Serialize(states)
                    )
                );
            }
        );

        static JsonElement ParseElement(string json)
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        return RegisteredParticipant.ForStandard(
            node,
            parallel,
            parallel.Success,
            parallel.Failed,
            owned
        );
    }

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
        var chatClient = await OpenAiCompatibleChatClients.CreateAsync(
            node.Client!,
            cancellationToken
        );
        if (node.Temperature is not null || node.MaxOutputTokens is not null)
        {
            chatClient = chatClient
                .AsBuilder()
                .ConfigureOptions(options =>
                {
                    options.Temperature = node.Temperature is { } temperature
                        ? (float)temperature
                        : null;
                    options.MaxOutputTokens = node.MaxOutputTokens;
                })
                .Build();
        }
        var builder = Agent
            .Create<JavaScriptState>(node.Id!, node.Instructions!, chatClient)
            .WithMessage(state => callbacks.Invoke(node.MessageCallback!, state.Json, ""));
        foreach (var directory in node.SkillDirectories ?? [])
        {
            builder.WithSkill(AgentSkill.FromDirectory(directory));
        }
        if (node.Output is { } outputContract)
        {
            using var schema = JsonDocument.Parse(outputContract.JsonSchema!);
            builder.WithJsonOutput(
                new AgentJsonOutputDefinition<JavaScriptState>(
                    schema.RootElement.Clone(),
                    outputContract.Instructions!,
                    candidate =>
                        ParseValidationProblems(
                            callbacks.Invoke(
                                outputContract.ValidateCallback!,
                                "",
                                candidate.GetRawText()
                            )
                        ),
                    outputContract.ValueType!,
                    outputContract.ValidateForCallback is null
                        ? null
                        : (state, candidate) =>
                            ParseValidationProblems(
                                callbacks.Invoke(
                                    outputContract.ValidateForCallback,
                                    state.Json,
                                    candidate.GetRawText()
                                )
                            )
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
                        capabilityContract.Instructions!,
                        schema.RootElement.Clone(),
                        request =>
                            ParseValidationProblems(
                                callbacks.Invoke(
                                    capabilityContract.ValidateCallback!,
                                    "",
                                    request.GetRawText()
                                )
                            ),
                        capabilityContract.ValidateForCallback is null
                            ? null
                            : (state, request) =>
                                ParseValidationProblems(
                                    callbacks.Invoke(
                                        capabilityContract.ValidateForCallback,
                                        state.Json,
                                        request.GetRawText()
                                    )
                                ),
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
        PipelineOutcomeSelector<JavaScriptState> failed,
        IReadOnlyList<RegisteredParticipant>? owned = null
    ) => new RegisteredStandard(c, s, success, failed, owned ?? []);

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
    PipelineOutcomeSelector<JavaScriptState> Failed,
    IReadOnlyList<RegisteredParticipant> Owned
) : RegisteredParticipant(Contract);

internal sealed record RegisteredInteraction(
    RegisteredNodeContract Contract,
    PipelineInteraction<JavaScriptState, string, string> Interaction
) : RegisteredParticipant(Contract);

internal sealed record RegisteredTerminal(
    RegisteredNodeContract Contract,
    IPipelineNode<JavaScriptState> Terminal
) : RegisteredParticipant(Contract);
