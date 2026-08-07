using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem;

public sealed class AgentOperation<TState>
{
    private const string AgentFailedOutcome = "agent.failed";
    private readonly AgentBlock<TState> _runtime;

    internal AgentOperation(AgentBlock<TState> runtime)
    {
        _runtime = runtime;
    }

    public async ValueTask<TResult> RunAsync<TResult>(
        TState state,
        Func<OperationResult<TState>, TResult> map,
        Func<FailureEvidence, TResult> mapFailure,
        CancellationToken cancellationToken
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var pipeline = PipelineExecutionEnvelope.Get(state);
        var result = await _runtime.ExecuteAsync(pipeline, cancellationToken);
        if (result.LatestOutcome?.Kind == AgentFailedOutcome)
        {
            result = result with { Disposition = PipelineRunDisposition.Failed };
            PipelineExecutionEnvelope.Set(result);
            return mapFailure(ToFailure(result.LatestOutcome));
        }
        PipelineExecutionEnvelope.Set(result);
        return map(OperationResult<TState>.From(result));
    }

    public async ValueTask<Outcome<TState>> RunAsync(
        TState state,
        CancellationToken cancellationToken
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var pipeline = PipelineExecutionEnvelope.Get(state);
        var result = await _runtime.ExecuteAsync(pipeline, cancellationToken);
        PipelineExecutionEnvelope.Set(result);
        return result.LatestOutcome?.Kind is StandardOutcomeKinds.Failed or AgentFailedOutcome
            ? new Outcome<TState>.Failed(result.State, ToFailure(result.LatestOutcome))
            : new Outcome<TState>.Success(result.State);
    }

    private static FailureEvidence ToFailure(BlockOutcome outcome) =>
        new(
            "agent.failed",
            outcome.Summary,
            outcome.Payload.ValueKind == System.Text.Json.JsonValueKind.Undefined
                ? null
                : outcome.Payload.GetRawText()
        );
}

public sealed record OperationResult<TState>(TState State, OperationOutcome Outcome)
{
    internal static OperationResult<TState> From(PipelineMessage<TState> message)
    {
        var outcome =
            message.LatestOutcome
            ?? throw new InvalidOperationException("Agent execution produced no outcome.");
        return new OperationResult<TState>(
            message.State,
            new OperationOutcome(outcome.Kind, outcome.Summary, outcome.Payload)
        );
    }
}

public sealed record OperationOutcome(
    string Kind,
    string Summary,
    System.Text.Json.JsonElement Payload
);

public static class PipelineOperation
{
    public static async ValueTask<TResult> RunAsync<TState, TResult>(
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

public sealed class AgentRuntime
{
    private readonly string _home;
    private readonly string? _executablePath;

    internal AgentRuntime(string home, string? executablePath)
    {
        _home = home;
        _executablePath = executablePath;
    }

    public AgentBuilder<TState> Create<TState>(
        string id,
        string profile,
        string instructions,
        IChatClient chatClient,
        Func<string, IChatClient>? profileChatClients = null
    ) => new(id, profile, instructions, chatClient, _home, _executablePath, profileChatClients);
}

public sealed class AgentBuilder<TState>
{
    private readonly string _id;
    private readonly string _profile;
    private readonly string _instructions;
    private readonly IChatClient _chatClient;
    private readonly string _home;
    private readonly string? _executablePath;
    private readonly Func<string, IChatClient>? _chatClientFactory;
    private Func<TState, string>? _message;
    private AdvancedAgentMessage<TState>? _contextMessage;
    private Func<TState, string>? _workspacePath;
    private Func<TState, bool>? _allowMutation;
    private StructuredOutputParser<TState>? _structuredOutput;
    private CheckpointPolicy<TState>? _checkpoint;
    private MessageAugmentation<TState>? _messageAugmentation;
    private AgentTurnPolicy<TState>? _turnPolicy;
    private StructuredOutputAcceptancePolicy<TState>? _structuredOutputAcceptance;
    private string? _structuredOutputCorrectionRequiredToolName;
    private ReceiptStateTransition<TState>? _receiptTransition;
    private string? _lifecycleActionSetIdentity;
    private IReadOnlyList<string> _lifecycleActionNames = [];
    private AgentSessionPolicy<TState>? _sessionPolicy;
    private AgentProfilePolicy<TState>? _profilePolicy;
    private AgentTeardownPolicy<TState>? _teardownPolicy;
    private ToolInterceptor<TState>? _toolInterceptor;
    private Action<ChatOptions>? _configureChatOptions;

    internal AgentBuilder(
        string id,
        string profile,
        string instructions,
        IChatClient chatClient,
        string home,
        string? executablePath,
        Func<string, IChatClient>? chatClientFactory
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);
        _id = id;
        _profile = profile;
        _instructions = instructions;
        _chatClient = chatClient;
        _home = home;
        _executablePath = executablePath;
        _chatClientFactory = chatClientFactory;
    }

    public AgentBuilder<TState> WithMessage(Func<TState, string> message)
    {
        _message = message;
        return this;
    }

    public AgentBuilder<TState> WithMessageFromContext(AdvancedAgentMessage<TState> message)
    {
        _contextMessage = message;
        return this;
    }

    public AgentBuilder<TState> WithSessionPolicy(AgentSessionPolicy<TState> policy)
    {
        _sessionPolicy = policy;
        return this;
    }

    public AgentBuilder<TState> WithWorkspace(
        Func<TState, string> path,
        Func<TState, bool> allowMutation,
        ToolInterceptor<TState>? toolInterceptor = null
    )
    {
        _workspacePath = path;
        _allowMutation = allowMutation;
        _toolInterceptor = toolInterceptor;
        return this;
    }

    public AgentBuilder<TState> WithStructuredOutput(
        StructuredOutputParser<TState> parser,
        Action<ChatOptions>? configureChatOptions = null,
        StructuredOutputAcceptancePolicy<TState>? acceptancePolicy = null,
        string? correctionRequiredToolName = null
    )
    {
        _structuredOutput = parser;
        _configureChatOptions = configureChatOptions;
        _structuredOutputAcceptance = acceptancePolicy;
        _structuredOutputCorrectionRequiredToolName = correctionRequiredToolName;
        return this;
    }

    public AgentBuilder<TState> WithLifecycleActions(
        string actionSetIdentity,
        IReadOnlyList<string> actionNames,
        ReceiptStateTransition<TState>? receiptTransition = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionSetIdentity);
        _lifecycleActionSetIdentity = actionSetIdentity;
        _lifecycleActionNames = actionNames;
        _receiptTransition = receiptTransition;
        return this;
    }

    public AgentBuilder<TState> WithCheckpoint(CheckpointPolicy<TState> policy)
    {
        _checkpoint = policy;
        return this;
    }

    public AgentBuilder<TState> WithMessageAugmentation(MessageAugmentation<TState> augmentation)
    {
        _messageAugmentation = augmentation;
        return this;
    }

    public AgentBuilder<TState> WithContinuationPolicy(AgentTurnPolicy<TState> policy)
    {
        _turnPolicy = policy;
        return this;
    }

    public AgentBuilder<TState> WithProfilePolicy(AgentProfilePolicy<TState> policy)
    {
        if (_chatClientFactory is null)
        {
            throw new InvalidOperationException(
                "Profile policy requires profile-backed chat-client resolution."
            );
        }

        _profilePolicy = policy;
        return this;
    }

    public AgentBuilder<TState> WithTeardownPolicy(AgentTeardownPolicy<TState> policy)
    {
        _teardownPolicy = policy;
        return this;
    }

    public AgentOperation<TState> Build(PipelineBuildContext? context = null)
    {
        if (_message is null && _contextMessage is null)
        {
            throw new InvalidOperationException($"Agent '{_id}' must configure a user message.");
        }
        if (_sessionPolicy is null)
        {
            throw new InvalidOperationException($"Agent '{_id}' must configure a session policy.");
        }
        if (_checkpoint is not null && _lifecycleActionSetIdentity is null)
        {
            throw new InvalidOperationException(
                $"Agent '{_id}' must select a lifecycle action set for checkpointing."
            );
        }

        var config = new AgentBlockConfig<TState>(
            _id,
            _profile,
            _instructions,
            _lifecycleActionNames,
            _message,
            _workspacePath,
            _allowMutation,
            _structuredOutput,
            _checkpoint,
            _messageAugmentation,
            _turnPolicy,
            _structuredOutputAcceptance,
            _structuredOutputCorrectionRequiredToolName,
            _receiptTransition,
            _lifecycleActionSetIdentity,
            _sessionPolicy,
            _profilePolicy,
            _teardownPolicy,
            _contextMessage
        );

        return new AgentOperation<TState>(
            new AgentBlock<TState>(
                config,
                _chatClient,
                _home,
                _executablePath,
                context?.AgentUpdate,
                _toolInterceptor,
                _configureChatOptions,
                _chatClientFactory
            )
        );
    }
}
