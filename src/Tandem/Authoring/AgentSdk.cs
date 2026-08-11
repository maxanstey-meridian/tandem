using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
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
    private static readonly JsonSerializerOptions _structuredOutputJsonOptions = new(
        JsonSerializerDefaults.Web
    )
    {
        Converters = { new JsonStringEnumConverter() },
    };
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
    private IReadOnlyList<
        Func<PipelineMessage<TState>, CancellationToken, ValueTask<string?>>
    > _messageAugmentations = [];
    private AgentTurnDescriptor<TState>? _turnPolicy;
    private IReadOnlyList<AgentCapabilityDescriptor<TState>> _capabilities = [];
    private IReadOnlyList<AgentSkillDescriptor> _skills = [];
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
    private IReadOnlyList<AgentStateGuardDescriptor<TState>> _stateGuards = [];
    private IReadOnlyList<AgentLatchedGateDescriptor> _latchedGates = [];

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

    public AgentBuilder<TState> WithSkill(AgentSkill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);
        if (_skills.Any(existing => existing.DirectoryPath == skill.DirectoryPath))
        {
            throw new InvalidOperationException(
                $"Agent '{_id}' has the skill directory '{skill.DirectoryPath}' more than once."
            );
        }

        _skills = [.. _skills, skill.Descriptor];
        return this;
    }

    public AgentBuilder<TState> WithSkills(params AgentSkill[] skills)
    {
        ArgumentNullException.ThrowIfNull(skills);
        foreach (var skill in skills)
        {
            WithSkill(skill);
        }
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
        IAgentOutputDefinition<TState, TOutput> output,
        Func<TState, TOutput, TState> apply
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(apply);
        _structuredOutput = new AgentStructuredOutputDescriptor<TState>(
            (response, state) =>
                AgentStructuredOutputPolicy.Parse<TOutput, TState>(
                    response,
                    _structuredOutputJsonOptions,
                    output.Validator,
                    output.ValidatorFor(state)
                ),
            Apply: (state, candidate) => apply(state, (TOutput)candidate),
            OutputType: typeof(TOutput),
            Instructions: output.Instructions,
            Examples: state =>
                output
                    .Examples(state)
                    .Select(example =>
                    {
                        ValidateExample(
                            example.Output,
                            output.Validator,
                            output.ValidatorFor(state)
                        );
                        return new AgentOutputExampleDescriptor(
                            example.Input,
                            JsonSerializer.Serialize(example.Output, _structuredOutputJsonOptions)
                        );
                    })
                    .ToArray()
        );
        _configureChatOptions = options =>
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema<TOutput>(
                serializerOptions: _structuredOutputJsonOptions
            );
        return this;
    }

    public AgentBuilder<TState> WithJsonOutput(
        AgentJsonOutputDefinition<TState> output,
        Func<TState, JsonElement, TState> apply
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.Instructions);
        ArgumentException.ThrowIfNullOrWhiteSpace(output.ValueType);
        ArgumentNullException.ThrowIfNull(output.Validate);
        ArgumentNullException.ThrowIfNull(apply);
        if (
            output.JsonSchema.ValueKind is not JsonValueKind.Object
            || !output.JsonSchema.TryGetProperty("type", out var rootType)
            || rootType.ValueKind is not JsonValueKind.String
            || rootType.GetString() != "object"
        )
        {
            throw new ArgumentException(
                "Output JSON schema must declare an object root with type 'object'.",
                nameof(output)
            );
        }
        var jsonSchema = output.JsonSchema.Clone();

        _structuredOutput = new AgentStructuredOutputDescriptor<TState>(
            (response, state) => ParseJsonOutput(response, state, output),
            Apply: (state, candidate) => apply(state, (JsonElement)candidate),
            OutputType: typeof(JsonElement),
            ValueType: output.ValueType,
            Instructions: output.Instructions
        );
        _configureChatOptions = options =>
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema(jsonSchema);
        return this;
    }

    private static AgentStructuredOutputResult<TState> ParseJsonOutput(
        string response,
        TState state,
        AgentJsonOutputDefinition<TState> output
    )
    {
        JsonElement candidate;
        try
        {
            var json = AgentStructuredJsonExtractor.Extract(response);
            using var document = JsonDocument.Parse(json);
            candidate = document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return new AgentStructuredOutputResult<TState>(
                null,
                [new AgentStructuredOutputProblem("$", exception.Message)],
                response
            );
        }
        if (candidate.ValueKind is not JsonValueKind.Object)
        {
            return new AgentStructuredOutputResult<TState>(
                null,
                [new AgentStructuredOutputProblem("$", "Response must contain a JSON object.")],
                response,
                candidate
            );
        }

        var problems = output
            .Validate(candidate)
            .Select(problem => new AgentStructuredOutputProblem(problem.Field, problem.Message))
            .ToArray();
        if (problems.Length == 0 && output.ValidateFor is not null)
        {
            problems = output
                .ValidateFor(state, candidate)
                .Select(problem => new AgentStructuredOutputProblem(problem.Field, problem.Message))
                .ToArray();
        }
        return problems.Length > 0
            ? new AgentStructuredOutputResult<TState>(null, problems, response, candidate)
            : new AgentStructuredOutputResult<TState>(
                new AgentStructuredOutcome<TState>(
                    StandardOutcomeKinds.Success,
                    "Succeeded",
                    candidate
                ),
                [],
                response,
                candidate
            );
    }

    private static void ValidateExample<TOutput>(
        TOutput example,
        IValidator<TOutput> intrinsic,
        IValidator<TOutput>? contextual
    )
    {
        var failures = intrinsic
            .Validate(example)
            .Errors.Concat(contextual?.Validate(example).Errors ?? [])
            .Select(error => error.ErrorMessage)
            .ToArray();
        if (failures.Length > 0)
        {
            throw new InvalidOperationException(
                $"Output example is invalid: {string.Join("; ", failures)}"
            );
        }
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
        _structuredOutput = descriptor with { OutputType = typeof(TOutput) };
        _configureChatOptions = options =>
            options.ResponseFormat = ChatResponseFormat.ForJsonSchema<TOutput>();
        return this;
    }

    internal AgentBuilder<TState> ConfigureOutputAcceptance(
        Type outputType,
        Func<
            PipelineMessage<TState>,
            AgentStructuredOutputResult<TState>,
            IReadOnlySet<ToolObservationDescriptor>,
            string,
            int,
            IReadOnlyList<AgentStructuredOutputProblem>
        > acceptance
    )
    {
        if (_structuredOutput is null)
        {
            throw new InvalidOperationException(
                "Output acceptance requires typed output. Call WithOutput(...) first."
            );
        }
        if (_structuredOutput.Accept is not null)
        {
            throw new InvalidOperationException("Output acceptance is already configured.");
        }
        if (_structuredOutput.OutputType != outputType)
        {
            throw new InvalidOperationException(
                $"Output acceptance for '{outputType.Name}' cannot decorate configured output "
                    + $"'{_structuredOutput.OutputType?.Name ?? "unknown"}'."
            );
        }
        _structuredOutput = _structuredOutput with { Accept = acceptance };
        return this;
    }

    internal AgentBuilder<TState> ConfigureOutputAcceptanceAsync(
        Type outputType,
        Func<
            PipelineMessage<TState>,
            AgentStructuredOutputResult<TState>,
            IReadOnlySet<ToolObservationDescriptor>,
            string,
            int,
            CancellationToken,
            ValueTask
        > acceptance
    )
    {
        if (_structuredOutput is null)
        {
            throw new InvalidOperationException(
                "Output acceptance requires typed output. Call WithOutput(...) first."
            );
        }
        if (_structuredOutput.AcceptAsync is not null)
        {
            throw new InvalidOperationException(
                "Asynchronous output acceptance is already configured."
            );
        }
        if (_structuredOutput.OutputType != outputType)
        {
            throw new InvalidOperationException(
                $"Output acceptance for '{outputType.Name}' cannot decorate configured output "
                    + $"'{_structuredOutput.OutputType?.Name ?? "unknown"}'."
            );
        }
        _structuredOutput = _structuredOutput with { AcceptAsync = acceptance };
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
        _latchedGates =
        [
            .. _latchedGates,
            new AgentLatchedGateDescriptor(
                "checkpoint-required",
                usage =>
                    usage.CurrentContextTokens + policy.MaxOutputTokens
                    >= policy.CheckpointAtTokens,
                new HashSet<ToolEffect> { ToolEffect.WorkspaceMutation },
                $"Context limit approaching. Call {policy.Capability.ToolName} before further mutation.",
                policy.Capability.CapabilityId,
                policy.Capability.ToolName,
                ResetSessionAfterRelease: true
            ),
        ];
        return this;
    }

    internal AgentBuilder<TState> ConfigureStateGuard(AgentStateGuardDescriptor<TState> guard)
    {
        if (_stateGuards.Any(existing => existing.Id == guard.Id))
        {
            throw new InvalidOperationException($"Agent '{_id}' has duplicate gate '{guard.Id}'.");
        }
        _stateGuards = [.. _stateGuards, guard];
        return this;
    }

    internal AgentBuilder<TState> ConfigureMessageAugmentation(
        Func<PipelineMessage<TState>, CancellationToken, ValueTask<string?>> augmentation
    )
    {
        _messageAugmentations = [.. _messageAugmentations, augmentation];
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
            _messageAugmentations,
            _turnPolicy,
            _continueSession,
            _profilePolicy,
            _retainConversation,
            _contextMessage,
            _implementationFactory,
            _timeout,
            _stateGuards,
            _latchedGates,
            _skills
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
