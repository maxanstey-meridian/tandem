using System.Text.Json;
using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Advanced;

public interface IPipelineAcceptanceUnitOfWork
{
    public ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken
    );
}

public static class AdvancedPipelineRunOptionsExtensions
{
    public static PipelineRunOptions WithAcceptanceUnitOfWork(
        this PipelineRunOptions options,
        IPipelineAcceptanceUnitOfWork unitOfWork
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        return options with { AcceptanceUnitOfWork = new AcceptanceUnitOfWorkAdapter(unitOfWork) };
    }

    private sealed class AcceptanceUnitOfWorkAdapter(IPipelineAcceptanceUnitOfWork unitOfWork)
        : Tandem.IPipelineAcceptanceUnitOfWork
    {
        public ValueTask<T> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken
        ) => unitOfWork.ExecuteAsync(operation, cancellationToken);
    }
}

public sealed record OperationResult<TState>(TState State, OperationOutcome Outcome)
{
    internal static OperationResult<TState> From(PipelineMessage<TState> message)
    {
        var outcome =
            message.LatestOutcome
            ?? throw new InvalidOperationException("Operation produced no outcome.");
        return new OperationResult<TState>(message.State, OperationOutcome.From(outcome));
    }
}

public sealed record OperationOutcome(
    string Kind,
    string BlockId,
    string Summary,
    JsonElement Payload = default,
    TimeSpan Duration = default
)
{
    internal static OperationOutcome From(BlockOutcome outcome) =>
        new(outcome.Kind, outcome.BlockId, outcome.Summary, outcome.Payload, outcome.Duration);

    internal BlockOutcome ToCore() => new(Kind, BlockId, Summary, Payload, Duration);
}

public sealed class PipelineOperationContext<TState>
{
    private readonly PipelineMessage<TState> _message;

    internal PipelineOperationContext(PipelineMessage<TState> message)
    {
        _message = message;
        RunId = message.Runtime.RunId;
        State = message.State;
        LatestOutcome = message.LatestOutcome is { } outcome
            ? OperationOutcome.From(outcome)
            : null;
    }

    public Guid RunId { get; }
    public TState State { get; }
    public OperationOutcome? LatestOutcome { get; }

    public ValueTask ObserveCommandOutputAsync(
        string stepId,
        string command,
        string output,
        int exitCode,
        CancellationToken cancellationToken
    ) =>
        _message.RunContext?.ObserveAsync(
            new PipelineCommandOutput(RunId, stepId, command, output, exitCode),
            cancellationToken
        ) ?? ValueTask.CompletedTask;
}

public sealed record AgentMessageOutcome(
    string Kind,
    string BlockId,
    string Summary,
    JsonElement Payload,
    TimeSpan Duration
);

public sealed record AgentMessageContext<TState>(
    Guid RunId,
    TState State,
    AgentMessageOutcome? LatestOutcome
);

public delegate string AdvancedAgentMessage<TState>(AgentMessageContext<TState> context);

public enum AgentConversationRetention
{
    Retain,
    Discard,
}

public sealed record AgentConversationDecision(AgentConversationRetention Retention);

public delegate AgentConversationDecision AgentConversationPolicy<TState>(
    AgentMessageContext<TState> context,
    AgentMessageOutcome outcome
);

public abstract record ToolInterceptionResult
{
    public sealed record Blocked(string Message) : ToolInterceptionResult;
}

public enum ToolEffect
{
    Read,
    WorkspaceMutation,
    LifecycleTransition,
    Unclassified,
}

public sealed record ToolInvocation(string Name, ToolEffect Effect);

public enum ToolEvidence
{
    None,
    RepositoryInspection,
}

public sealed record ToolObservation(string Name, ToolEffect Effect, ToolEvidence Evidence);

public delegate ValueTask<ToolInterceptionResult?> ToolInterceptor<TState>(
    AgentMessageContext<TState> context,
    ToolInvocation invocation,
    CancellationToken cancellationToken
);

public delegate ValueTask<string?> MessageAugmentation<TState>(
    AgentMessageContext<TState> context,
    CancellationToken cancellationToken
);

public sealed record AgentTurnObservation<TState>(
    AgentMessageContext<TState> Context,
    string AssistantText,
    IReadOnlyList<string> ToolNames,
    bool HasAcceptedLifecycleOutcome,
    int ContinuationAttempt
);

public sealed record AgentTurnDirective(string Prompt, string? RequiredToolName = null);

public delegate ValueTask<AgentTurnDirective?> AgentTurnContinuationPolicy<TState>(
    AgentTurnObservation<TState> observation,
    CancellationToken cancellationToken
);

public sealed record AgentTurnPolicy<TState>
{
    public AgentTurnPolicy(
        int maxContinuationAttempts,
        AgentTurnContinuationPolicy<TState> @continue
    )
    {
        if (maxContinuationAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxContinuationAttempts));
        }

        MaxContinuationAttempts = maxContinuationAttempts;
        Continue = @continue;
    }

    public int MaxContinuationAttempts { get; }
    public AgentTurnContinuationPolicy<TState> Continue { get; }
}

public sealed record AgentProfileDecision(string ProfileName, string Reason);

public delegate AgentProfileDecision AgentProfilePolicy<TState>(TState state);

public sealed record AgentCheckpointContext<TState>(TState State, int CurrentContextTokens);

public sealed record CheckpointPolicy<TState>(
    int ContextWindowTokens,
    int MaxOutputTokens,
    int CheckpointAtPercent,
    AgentCapability<TState> Capability,
    string Instructions,
    Func<AgentCheckpointContext<TState>, string> UserMessage
);

public sealed record AgentStateGuard<TState>(
    string Id,
    Func<TState, bool> IsActive,
    IReadOnlySet<ToolEffect> Blocks,
    string Message,
    AgentCapability<TState>? Remediation = null
);

public static class AgentProfiles
{
    public static AgentBuilder<TState> Create<TState>(
        string id,
        string profile,
        string instructions,
        IChatClient chatClient,
        Func<string, IChatClient> profileChatClients
    ) =>
        AgentBuilder<TState>.CreateProfiled(
            id,
            profile,
            instructions,
            chatClient,
            profileChatClients
        );
}

public static class AdvancedAgentBuilderExtensions
{
    public static AgentBuilder<TState> UseHarness<TState>(
        this AgentBuilder<TState> builder,
        string harnessInstructions
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(harnessInstructions);
        return builder.ConfigureImplementation(context =>
            HarnessAgentImplementation.Create(context, harnessInstructions)
        );
    }

    public static AgentBuilder<TState> WithMessageFromContext<TState>(
        this AgentBuilder<TState> builder,
        AdvancedAgentMessage<TState> message
    ) => builder.ConfigureMessageFromContext(pipeline => message(ToContext(pipeline)));

    public static AgentBuilder<TState> WithWorkspace<TState>(
        this AgentBuilder<TState> builder,
        Func<TState, string> path,
        Func<TState, bool> allowMutation,
        ToolInterceptor<TState>? toolInterceptor = null
    ) =>
        builder.ConfigureWorkspace(
            path,
            allowMutation,
            toolInterceptor is null
                ? null
                : async (message, toolName, effect, cancellationToken) =>
                {
                    var result = await toolInterceptor(
                        ToContext(message),
                        new ToolInvocation(
                            toolName,
                            effect switch
                            {
                                Infrastructure.ToolEffect.Read => ToolEffect.Read,
                                Infrastructure.ToolEffect.WorkspaceMutation =>
                                    ToolEffect.WorkspaceMutation,
                                Infrastructure.ToolEffect.LifecycleTransition =>
                                    ToolEffect.LifecycleTransition,
                                _ => ToolEffect.Unclassified,
                            }
                        ),
                        cancellationToken
                    );
                    return result is ToolInterceptionResult.Blocked blocked
                        ? blocked.Message
                        : null;
                }
        );

    public static AgentBuilder<TState> WithStructuredOutput<TState>(
        this AgentBuilder<TState> builder,
        StructuredOutputParser<TState> parser,
        Action<ChatOptions>? configureChatOptions = null,
        StructuredOutputAcceptancePolicy<TState>? acceptancePolicy = null,
        string? correctionRequiredToolName = null
    ) =>
        builder.ConfigureStructuredOutput(
            StructuredOutputDescriptors.Create(
                parser,
                acceptancePolicy,
                correctionRequiredToolName
            ),
            configureChatOptions
        );

    public static AgentBuilder<TState> WithOutput<TState, TOutput>(
        this AgentBuilder<TState> builder,
        StructuredOutputParser<TState> parser,
        StructuredOutputAcceptancePolicy<TState>? acceptancePolicy = null,
        string? correctionRequiredToolName = null
    ) =>
        builder.ConfigureOutput<TOutput>(
            StructuredOutputDescriptors.Create(parser, acceptancePolicy, correctionRequiredToolName)
        );

    public static AgentBuilder<TState> RequireOutputAcceptance<TState, TOutput>(
        this AgentBuilder<TState> builder,
        OutputAcceptancePolicy<TState, TOutput> acceptance
    ) =>
        builder.ConfigureOutputAcceptance(
            typeof(TOutput),
            StructuredOutputDescriptors.Accept(acceptance)
        );

    public static AgentBuilder<TState> WithOutputAcceptance<TState, TOutput>(
        this AgentBuilder<TState> builder,
        OutputAcceptance<TState, TOutput> acceptance
    ) =>
        builder.ConfigureOutputAcceptanceAsync(
            typeof(TOutput),
            StructuredOutputDescriptors.AcceptAsync(acceptance)
        );

    public static AgentBuilder<TState> WithCheckpoint<TState>(
        this AgentBuilder<TState> builder,
        CheckpointPolicy<TState> policy
    ) =>
        builder.ConfigureCheckpoint(
            new AgentCheckpointDescriptor<TState>(
                policy.ContextWindowTokens,
                policy.MaxOutputTokens,
                policy.CheckpointAtPercent,
                policy.Capability.Descriptor,
                policy.Instructions,
                (state, currentContextTokens) =>
                    policy.UserMessage(
                        new AgentCheckpointContext<TState>(state, currentContextTokens)
                    )
            )
        );

    public static AgentBuilder<TState> WithStateGuard<TState>(
        this AgentBuilder<TState> builder,
        AgentStateGuard<TState> guard
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(guard.Id);
        ArgumentNullException.ThrowIfNull(guard.IsActive);
        ArgumentNullException.ThrowIfNull(guard.Blocks);
        ArgumentException.ThrowIfNullOrWhiteSpace(guard.Message);
        if (guard.Blocks.Count == 0 || guard.Blocks.Contains(ToolEffect.Unclassified))
        {
            throw new ArgumentException(
                "State guards require at least one classified blocked effect.",
                nameof(guard)
            );
        }
        return builder.ConfigureStateGuard(
            new AgentStateGuardDescriptor<TState>(
                guard.Id,
                guard.IsActive,
                guard.Blocks.Select(ToInfrastructure).ToHashSet(),
                guard.Message,
                guard.Remediation?.Descriptor.ToolName
            )
        );
    }

    private static Infrastructure.ToolEffect ToInfrastructure(ToolEffect effect) =>
        effect switch
        {
            ToolEffect.Read => Infrastructure.ToolEffect.Read,
            ToolEffect.WorkspaceMutation => Infrastructure.ToolEffect.WorkspaceMutation,
            ToolEffect.LifecycleTransition => Infrastructure.ToolEffect.LifecycleTransition,
            _ => throw new ArgumentOutOfRangeException(nameof(effect)),
        };

    public static AgentBuilder<TState> WithMessageAugmentation<TState>(
        this AgentBuilder<TState> builder,
        MessageAugmentation<TState> augmentation
    ) =>
        builder.ConfigureMessageAugmentation(
            (message, cancellationToken) => augmentation(ToContext(message), cancellationToken)
        );

    public static AgentBuilder<TState> WithContinuationPolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentTurnPolicy<TState> policy
    ) =>
        builder.ConfigureContinuationPolicy(
            new AgentTurnDescriptor<TState>(
                policy.MaxContinuationAttempts,
                async (
                    message,
                    assistantText,
                    toolNames,
                    hasAcceptedLifecycleOutcome,
                    continuationAttempt,
                    cancellationToken
                ) =>
                {
                    var directive = await policy.Continue(
                        new AgentTurnObservation<TState>(
                            ToContext(message),
                            assistantText,
                            toolNames,
                            hasAcceptedLifecycleOutcome,
                            continuationAttempt
                        ),
                        cancellationToken
                    );
                    return directive is null
                        ? null
                        : new AgentTurnDirectiveDescriptor(
                            directive.Prompt,
                            directive.RequiredToolName
                        );
                }
            )
        );

    public static AgentBuilder<TState> WithProfilePolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentProfilePolicy<TState> policy
    ) =>
        builder.ConfigureProfilePolicy(state =>
        {
            var decision = policy(state);
            return new AgentProfileSelection(decision.ProfileName, decision.Reason);
        });

    public static AgentBuilder<TState> WithConversationPolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentConversationPolicy<TState> policy
    ) =>
        builder.ConfigureConversationPolicy(
            (message, outcome) =>
                policy(ToContext(message), ToOutcome(outcome)).Retention
                == AgentConversationRetention.Retain
        );

    private static AgentMessageContext<TState> ToContext<TState>(PipelineMessage<TState> message) =>
        new(
            message.Runtime.RunId,
            message.State,
            message.LatestOutcome is { } outcome ? ToOutcome(outcome) : null
        );

    private static AgentMessageOutcome ToOutcome(BlockOutcome outcome) =>
        new(outcome.Kind, outcome.BlockId, outcome.Summary, outcome.Payload, outcome.Duration);
}

public static class PipelineOperation
{
    internal static ValueTask ObserveCommandOutputAsync<TState>(
        PipelineMessage<TState> pipeline,
        string stepId,
        string command,
        string output,
        int exitCode,
        CancellationToken cancellationToken
    ) =>
        pipeline.RunContext?.ObserveAsync(
            new PipelineCommandOutput(pipeline.Runtime.RunId, stepId, command, output, exitCode),
            cancellationToken
        ) ?? ValueTask.CompletedTask;

    public static async ValueTask<Outcome<TState>> RunOutcomeAsync<TState>(
        TState state,
        Func<PipelineOperationContext<TState>, ValueTask<OperationResult<TState>>> execute,
        Func<OperationResult<TState>, Outcome<TState>> map
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var pipeline = PipelineExecutionEnvelope.Get(state);
        var result = await execute(new PipelineOperationContext<TState>(pipeline));
        PipelineExecutionEnvelope.Set(
            pipeline with
            {
                State = result.State,
                LatestOutcome = result.Outcome.ToCore(),
            }
        );
        return map(result);
    }

    internal static async ValueTask<Outcome<TState>> RunOutcomeAsync<TState>(
        TState state,
        Func<PipelineMessage<TState>, ValueTask<PipelineMessage<TState>>> execute,
        Func<OperationResult<TState>, Outcome<TState>> map
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var result = await execute(PipelineExecutionEnvelope.Get(state));
        PipelineExecutionEnvelope.Set(result);
        return map(OperationResult<TState>.From(result));
    }

    internal static async ValueTask<TState> RunStateAsync<TState>(
        TState state,
        Func<PipelineMessage<TState>, ValueTask<PipelineMessage<TState>>> execute
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var result = await execute(PipelineExecutionEnvelope.Get(state));
        PipelineExecutionEnvelope.Set(result);
        return result.State;
    }

    internal static async ValueTask<TResult> RunAsync<TState, TResult>(
        Func<ValueTask<PipelineMessage<TState>>> execute,
        Func<OperationResult<TState>, TResult> map
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var result = await execute();
        PipelineExecutionEnvelope.Set(result);
        return map(OperationResult<TState>.From(result));
    }
}
