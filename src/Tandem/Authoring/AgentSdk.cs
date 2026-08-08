using System.ComponentModel;
using System.Text.Json;
using FluentValidation;
using Microsoft.Extensions.AI;
using Tandem.Domain;
using Tandem.Infrastructure;
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

public static class Agent
{
    public static AgentBuilder<TState> Create<TState>(
        string id,
        string instructions,
        IChatClient chatClient
    ) => new(id, id, instructions, chatClient, chatClientFactory: null);
}

public sealed class AgentBuilder<TState>
{
    private static readonly TimeSpan _maximumTimeout = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private readonly string _id;
    private readonly string _profile;
    private readonly string _instructions;
    private readonly IChatClient _chatClient;
    private readonly Func<string, IChatClient>? _chatClientFactory;
    private Func<TState, string>? _message;
    private Func<PipelineMessage<TState>, string>? _contextMessage;
    private Func<TState, string>? _workspacePath;
    private Func<TState, bool>? _allowMutation;
    private AgentStructuredOutputDescriptor<TState>? _structuredOutput;
    private AgentCheckpointDescriptor<TState>? _checkpoint;
    private Func<
        PipelineMessage<TState>,
        CancellationToken,
        ValueTask<string?>
    >? _messageAugmentation;
    private AgentTurnDescriptor<TState>? _turnPolicy;
    private IReadOnlyList<AgentCapabilityDescriptor<TState>> _capabilities = [];
    private bool _continueSession;
    private Func<TState, AgentProfileSelection>? _profilePolicy;
    private Func<PipelineMessage<TState>, BlockOutcome, bool>? _retainConversation;
    private Func<
        PipelineMessage<TState>,
        string,
        ToolEffect?,
        CancellationToken,
        ValueTask<string?>
    >? _toolInterceptor;
    private Action<ChatOptions>? _configureChatOptions;
    private AgentImplementationFactory? _implementationFactory;
    private TimeSpan? _timeout;

    internal AgentBuilder(
        string id,
        string profile,
        string instructions,
        IChatClient chatClient,
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
        _chatClientFactory = chatClientFactory;
    }

    internal static AgentBuilder<TState> CreateProfiled(
        string id,
        string profile,
        string instructions,
        IChatClient chatClient,
        Func<string, IChatClient> profileChatClients
    ) => new(id, profile, instructions, chatClient, profileChatClients);

    public AgentBuilder<TState> WithMessage(Func<TState, string> message)
    {
        _message = message;
        return this;
    }

    internal AgentBuilder<TState> ConfigureMessageFromContext(
        Func<PipelineMessage<TState>, string> message
    )
    {
        _contextMessage = message;
        return this;
    }

    public AgentBuilder<TState> ContinueSession()
    {
        _continueSession = true;
        return this;
    }

    public AgentBuilder<TState> WithTimeout(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero || timeout > _maximumTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
        _timeout = timeout;
        return this;
    }

    internal AgentBuilder<TState> ConfigureImplementation(AgentImplementationFactory factory)
    {
        _implementationFactory = factory;
        return this;
    }

    internal AgentBuilder<TState> ConfigureWorkspace(
        Func<TState, string> path,
        Func<TState, bool> allowMutation,
        Func<
            PipelineMessage<TState>,
            string,
            ToolEffect?,
            CancellationToken,
            ValueTask<string?>
        >? toolInterceptor = null
    )
    {
        _workspacePath = path;
        _allowMutation = allowMutation;
        _toolInterceptor = toolInterceptor;
        return this;
    }

    internal AgentBuilder<TState> ConfigureStructuredOutput(
        AgentStructuredOutputDescriptor<TState> descriptor,
        Action<ChatOptions>? configureChatOptions = null
    )
    {
        _structuredOutput = descriptor;
        _configureChatOptions = configureChatOptions;
        return this;
    }

    public AgentBuilder<TState> WithOutput<TOutput>(
        IValidator<TOutput> validator,
        Func<TState, TOutput, TState> apply
    )
    {
        _structuredOutput = new AgentStructuredOutputDescriptor<TState>(
            (response, state) =>
                AgentStructuredOutputPolicy.Parse(
                    response,
                    state,
                    JsonSerializerOptions.Web,
                    validator,
                    (output, current) =>
                        new AgentStructuredOutcome<TState>(
                            StandardOutcomeKinds.Success,
                            "Succeeded",
                            JsonSerializer.SerializeToElement(output, JsonSerializerOptions.Web),
                            apply(current, output)
                        )
                )
        );
        _configureChatOptions = options =>
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema<TOutput>();
        return this;
    }

    public AgentBuilder<TState> WithCapability(AgentCapability<TState> capability)
    {
        ArgumentNullException.ThrowIfNull(capability);
        AddCapability(capability.Descriptor);
        return this;
    }

    internal AgentBuilder<TState> ConfigureOutput<TOutput>(
        AgentStructuredOutputDescriptor<TState> descriptor
    )
    {
        _structuredOutput = descriptor;
        _configureChatOptions = options =>
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema<TOutput>();
        return this;
    }

    private void AddCapability(AgentCapabilityDescriptor<TState> capability)
    {
        var existing = _capabilities.FirstOrDefault(existing =>
            existing.ToolName == capability.ToolName
        );
        if (ReferenceEquals(existing, capability))
        {
            return;
        }
        if (existing is not null)
        {
            throw new InvalidOperationException(
                $"Agent '{_id}' has multiple capabilities named '{capability.ToolName}'."
            );
        }
        _capabilities = [.. _capabilities, capability];
    }

    internal AgentBuilder<TState> ConfigureCapability(AgentCapabilityDescriptor<TState> capability)
    {
        AddCapability(capability);
        return this;
    }

    internal AgentBuilder<TState> ConfigureCheckpoint(AgentCheckpointDescriptor<TState> policy)
    {
        _checkpoint = policy;
        AddCapability(policy.Capability);
        return this;
    }

    internal AgentBuilder<TState> ConfigureMessageAugmentation(
        Func<PipelineMessage<TState>, CancellationToken, ValueTask<string?>> augmentation
    )
    {
        _messageAugmentation = augmentation;
        return this;
    }

    internal AgentBuilder<TState> ConfigureContinuationPolicy(AgentTurnDescriptor<TState> policy)
    {
        _turnPolicy = policy;
        return this;
    }

    internal AgentBuilder<TState> ConfigureProfilePolicy(Func<TState, AgentProfileSelection> policy)
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
        Func<PipelineMessage<TState>, BlockOutcome, bool> retainConversation
    )
    {
        _retainConversation = retainConversation;
        return this;
    }

    public AgentDefinition<TState> Build()
    {
        if (_message is null && _contextMessage is null)
        {
            throw new InvalidOperationException($"Agent '{_id}' must configure a user message.");
        }
        if (_workspacePath is not null && _implementationFactory is null)
        {
            throw new InvalidOperationException(
                $"Agent '{_id}' configures a workspace, which requires explicit Harness execution. "
                    + "Call UseHarness() from Tandem.Advanced."
            );
        }
        var config = new AgentBlockConfig<TState>(
            _id,
            _profile,
            _instructions,
            _capabilities,
            _message,
            _workspacePath,
            _allowMutation,
            _structuredOutput,
            _checkpoint,
            _messageAugmentation,
            _turnPolicy,
            _continueSession,
            _profilePolicy,
            _retainConversation,
            _contextMessage,
            _implementationFactory,
            _timeout
        );

        return new AgentDefinition<TState>(
            _id,
            new AgentOperation<TState>(
                new AgentBlock<TState>(
                    config,
                    _chatClient,
                    onUpdate: null,
                    _toolInterceptor,
                    _configureChatOptions,
                    _chatClientFactory
                )
            )
        );
    }
}
