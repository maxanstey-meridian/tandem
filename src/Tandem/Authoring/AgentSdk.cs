using System.ComponentModel;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure.Blocks;

namespace Tandem;

internal sealed class AgentOperation<TState>
{
    private const string AgentFailedOutcome = "agent.failed";
    private readonly Func<
        PipelineMessage<TState>,
        CancellationToken,
        ValueTask<PipelineMessage<TState>>
    > _execute;

    internal AgentOperation(AgentBlock<TState> runtime)
        : this(runtime.ExecuteAsync) { }

    internal AgentOperation(
        Func<PipelineMessage<TState>, CancellationToken, ValueTask<PipelineMessage<TState>>> execute
    )
    {
        _execute = execute;
    }

    public async ValueTask<Outcome<TState>> RunAsync(
        TState state,
        CancellationToken cancellationToken
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var pipeline = PipelineExecutionEnvelope.Get(state);
        var result = await _execute(pipeline, cancellationToken);
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

public sealed class AgentDefinition<TState> : IStandardOutcomePipelineStep<TState>
{
    private readonly GeneratedOutcomeStepDescriptor<TState> _descriptor;

    internal AgentDefinition(string id, AgentOperation<TState> operation)
    {
        Id = id;
        _descriptor = new GeneratedOutcomeStepDescriptor<TState>(id, operation.RunAsync);
    }

    public string Id { get; }

    [EditorBrowsable(EditorBrowsableState.Never)]
    public PipelineNodeDescriptor Descriptor => _descriptor;
    public PipelineOutcomeSelector<TState> Success => new(this, failed: false);
    public PipelineOutcomeSelector<TState> Failed => new(this, failed: true);
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
    private AgentConversationPolicy<TState>? _conversationPolicy;
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

    internal AgentBuilder<TState> ConfigureMessageFromContext(AdvancedAgentMessage<TState> message)
    {
        _contextMessage = message;
        return this;
    }

    public AgentBuilder<TState> WithSessionPolicy(AgentSessionPolicy<TState> policy)
    {
        _sessionPolicy = policy;
        return this;
    }

    internal AgentBuilder<TState> ConfigureWorkspace(
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

    internal AgentBuilder<TState> ConfigureStructuredOutput(
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

    public AgentBuilder<TState> WithOutput<TOutput>(
        IValidator<TOutput> validator,
        Func<TState, TOutput, TState> apply
    )
    {
        _structuredOutput = (response, state) =>
            StructuredOutputPolicy.Parse(
                response,
                state,
                JsonSerializerOptions.Web,
                validator,
                (output, current) =>
                {
                    return new StructuredOutcome<TState>(
                        StandardOutcomeKinds.Success,
                        "Succeeded",
                        JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web),
                        apply(current, output)
                    );
                }
            );
        _configureChatOptions = options =>
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema<TOutput>();
        return this;
    }

    internal AgentBuilder<TState> ConfigureOutput<TOutput>(
        StructuredOutputParser<TState> parser,
        StructuredOutputAcceptancePolicy<TState>? acceptancePolicy = null,
        string? correctionRequiredToolName = null
    )
    {
        _structuredOutput = parser;
        _structuredOutputAcceptance = acceptancePolicy;
        _structuredOutputCorrectionRequiredToolName = correctionRequiredToolName;
        _configureChatOptions = options =>
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema<TOutput>();
        return this;
    }

    private void AddCapability(AgentCapability<TState> capability)
    {
        if (
            _lifecycleActionSetIdentity is not null
            && !string.Equals(
                _lifecycleActionSetIdentity,
                capability.Identity,
                StringComparison.Ordinal
            )
        )
        {
            throw new InvalidOperationException(
                "All capabilities on an agent must share one identity."
            );
        }
        if (_lifecycleActionNames.Contains(capability.ToolName, StringComparer.Ordinal))
        {
            return;
        }
        _lifecycleActionSetIdentity = capability.Identity;
        _lifecycleActionNames = [.. _lifecycleActionNames, capability.ToolName];
        var current = _receiptTransition;
        _receiptTransition = current is null
            ? capability.Transition
            : (state, kind, payload) =>
                capability.Transition(current(state, kind, payload), kind, payload);
    }

    internal AgentBuilder<TState> ConfigureCapability(AgentCapability<TState> capability)
    {
        AddCapability(capability);
        return this;
    }

    internal AgentBuilder<TState> ConfigureCheckpoint(CheckpointPolicy<TState> policy)
    {
        _checkpoint = policy;
        AddCapability(policy.Capability);
        return this;
    }

    internal AgentBuilder<TState> ConfigureMessageAugmentation(
        MessageAugmentation<TState> augmentation
    )
    {
        _messageAugmentation = augmentation;
        return this;
    }

    internal AgentBuilder<TState> ConfigureContinuationPolicy(AgentTurnPolicy<TState> policy)
    {
        _turnPolicy = policy;
        return this;
    }

    internal AgentBuilder<TState> ConfigureProfilePolicy(AgentProfilePolicy<TState> policy)
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

    internal AgentBuilder<TState> ConfigureConversationPolicy(
        AgentConversationPolicy<TState> policy
    )
    {
        _conversationPolicy = policy;
        return this;
    }

    public AgentDefinition<TState> Build()
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
            _conversationPolicy,
            _contextMessage
        );

        return new AgentDefinition<TState>(
            _id,
            new AgentOperation<TState>(
                new AgentBlock<TState>(
                    config,
                    _chatClient,
                    _home,
                    _executablePath,
                    AgentUpdates.Publish,
                    _toolInterceptor,
                    _configureChatOptions,
                    _chatClientFactory
                )
            )
        );
    }
}
