using System.Text.Json;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

namespace Tandem.Domain;

internal sealed record AgentBlockConfig<TState>(
    string StepId,
    string ProfileName,
    string SystemInstructions,
    IReadOnlyList<AgentCapabilityDescriptor<TState>> Capabilities,
    Func<TState, string>? UserMessage,
    AgentWorkspaceDescriptor<TState>? Workspace,
    AgentStructuredOutputDescriptor<TState>? StructuredOutput = null,
    AgentCheckpointDescriptor<TState>? Checkpoint = null,
    IReadOnlyList<
        Func<PipelineMessage<TState>, CancellationToken, ValueTask<string?>>
    >? MessageAugmentations = null,
    AgentTurnDescriptor<TState>? TurnPolicy = null,
    bool ContinueSession = false,
    Func<TState, AgentProfileSelection>? ProfilePolicy = null,
    Func<PipelineMessage<TState>, BlockOutcome, bool>? RetainConversation = null,
    AgentImplementationFactory? ImplementationFactory = null,
    TimeSpan? Timeout = null,
    IReadOnlyList<AgentStateGuardDescriptor<TState>>? StateGuards = null,
    IReadOnlyList<AgentLatchedGateDescriptor>? LatchedGates = null,
    IReadOnlyList<AgentSkillDescriptor>? Skills = null,
    AgentContextBudgetDescriptor? ContextBudget = null,
    AgentModelRequestOptions? ModelRequestOptions = null
);

internal sealed record AgentContextBudgetDescriptor(
    int ContextWindowTokens,
    int MaxOutputTokens,
    bool DisableCompaction
);

internal sealed record AgentWorkspaceDescriptor<TState>(
    Func<TState, string> Path,
    Func<TState, IReadOnlyList<AgentCommandDescriptor>> Commands,
    IReadOnlyList<AgentToolGroupDescriptor<TState>> ToolGroups,
    IReadOnlyDictionary<string, AgentWorkspaceToolDescriptor>? RegisteredTools = null
);

internal sealed record AgentWorkspaceToolDescriptor(
    string Name,
    Func<string, Microsoft.Extensions.AI.AIFunction> Create,
    ToolEffect Effect,
    ToolEvidence Evidence
);

internal sealed record AgentToolGroupDescriptor<TState>(
    Func<TState, bool> IsAvailable,
    IReadOnlyList<AgentToolSelectionDescriptor> Tools
);

internal enum AgentToolSelectionKind
{
    BuiltIn,
    Commands,
    Registered,
}

internal sealed record AgentToolSelectionDescriptor(
    AgentToolSelectionKind Kind,
    string? Name = null
);

internal sealed record AgentStateGuardDescriptor<TState>(
    string Id,
    Func<TState, bool> IsActive,
    IReadOnlySet<ToolEffect> BlockedEffects,
    string Message,
    string? RemediationCapabilityName
);

internal sealed record AgentLatchedGateDescriptor(
    string Id,
    Func<AgentUsage, bool> Trigger,
    IReadOnlySet<ToolEffect> BlockedEffects,
    string Message,
    string ReleaseCapabilityId,
    string ReleaseCapabilityName,
    bool ResetSessionAfterRelease
);

internal sealed record StructuredOutputAttempt<TState>(
    PipelineMessage<TState> Message,
    string StepId,
    string AcceptedOutputId,
    IReadOnlySet<ToolObservation> Tools,
    IReadOnlyList<ToolInvocationObservation> ToolInvocations,
    int Attempt
);

/// <summary>Accepts a valid output, or returns the problems that send it back for correction.</summary>
internal delegate ValueTask<IReadOnlyList<ValidationProblem>> StructuredOutputAcceptance<
    TState,
    in TOutput
>(StructuredOutputAttempt<TState> attempt, TOutput output, CancellationToken cancellationToken);

internal abstract record AgentStructuredOutputDescriptor<TState>(
    string? Instructions,
    JsonElement? JsonSchema,
    ChatResponseFormat? ResponseFormat
)
{
    public abstract IReadOnlyList<AgentOutputExampleDescriptor> Examples(TState state);

    public abstract ValueTask<AgentStructuredOutputResult<TState>> EvaluateAsync(
        string response,
        StructuredOutputAttempt<TState> attempt,
        CancellationToken cancellationToken
    );
}

internal sealed record AgentStructuredOutputDescriptor<TState, TOutput>(
    Func<string, TState, ParsedOutput<TOutput>> Parse,
    Func<TState, TOutput, TState> Apply,
    string ValueType,
    string? Instructions,
    JsonElement? JsonSchema = null,
    ChatResponseFormat? ResponseFormat = null
) : AgentStructuredOutputDescriptor<TState>(Instructions, JsonSchema, ResponseFormat)
{
    public Func<TState, IReadOnlyList<AgentOutputExampleDescriptor>>? ExampleFactory { get; init; }
    public StructuredOutputAcceptance<TState, TOutput>? Accept { get; init; }

    public override IReadOnlyList<AgentOutputExampleDescriptor> Examples(TState state) =>
        ExampleFactory?.Invoke(state) ?? [];

    public override async ValueTask<AgentStructuredOutputResult<TState>> EvaluateAsync(
        string response,
        StructuredOutputAttempt<TState> attempt,
        CancellationToken cancellationToken
    )
    {
        var message = attempt.Message;
        var parsed = Parse(response, message.State);
        if (parsed.Problems.Count > 0)
        {
            return AgentStructuredOutputResult<TState>.Rejected(parsed.Problems, response);
        }
        var output = parsed.Value!;
        if (Accept is not null)
        {
            var problems = await Accept(attempt, output, cancellationToken);
            if (problems.Count > 0)
            {
                return AgentStructuredOutputResult<TState>.Rejected(problems, response);
            }
        }

        var payload = JsonSerializer.SerializeToElement(output, TandemJson.TypedContract);
        if (message.RunContext is { } runContext)
        {
            await runContext.ObserveAsync(
                new OutputAccepted<TOutput>(
                    message.Runtime.RunId,
                    attempt.StepId,
                    attempt.AcceptedOutputId,
                    StandardOutcomeKinds.Success,
                    ValueType,
                    runContext.ShouldPersist(attempt.StepId) ? payload : null,
                    output
                ),
                cancellationToken
            );
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Problems: [], response, payload, Apply(message.State, output));
    }
}

internal sealed record AgentOutputExampleDescriptor(string Input, string Output);

internal sealed record AgentTurnDescriptor<TState>(
    int MaxContinuationAttempts,
    Func<
        PipelineMessage<TState>,
        string,
        IReadOnlyList<string>,
        bool,
        int,
        CancellationToken,
        ValueTask<AgentTurnDirectiveDescriptor?>
    > Continue
);

internal sealed record AgentTurnDirectiveDescriptor(string Prompt, string? RequiredToolName);

internal sealed record AgentCheckpointDescriptor<TState>(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent,
    AgentCapabilityDescriptor<TState> Capability,
    string Instructions,
    Func<TState, int, string> UserMessage,
    bool ResetSessionAfterRelease = true,
    bool DisableCompaction = false
)
{
    public int CheckpointAtTokens =>
        (int)Math.Floor(ContextWindowTokens * (CheckpointAtPercent / 100.0));
}

internal sealed record AgentProfileSelection(string ProfileName, string Reason);
