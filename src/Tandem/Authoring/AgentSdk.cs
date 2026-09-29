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
    {
        _execute = runtime.ExecuteAsync;
    }

    public async ValueTask<Outcome<TState>> RunAsync(
        TState state,
        CancellationToken cancellationToken
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var pipeline = PipelineExecutionEnvelope.Get(state);
        var result = await _execute(pipeline, cancellationToken);
        result = result with
        {
            RunContext = pipeline.RunContext,
            ParallelContext = pipeline.ParallelContext,
        };
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

public sealed class AgentDefinition<TState>
    : IStandardOutcomePipelineStep<TState>,
        ICollectionAgent,
        ICollectionAgentBinding
{
    private readonly GeneratedOutcomeStepDescriptor<TState> _descriptor;

    private readonly Func<string, AgentOperation<TState>> _create;

    internal AgentDefinition(string id, Func<string, AgentOperation<TState>> create)
    {
        Id = id;
        _create = create;
        _descriptor = new GeneratedOutcomeStepDescriptor<TState>(id, create(id).RunAsync);
    }

    ICollectionAgent ICollectionAgentBinding.BindTo(string id) =>
        new AgentDefinition<TState>(id, _create);

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

public enum AgentReasoningEffort
{
    None,
    Low,
    Medium,
    High,
}

public sealed class AgentModelRequestOptions
{
    public AgentModelRequestOptions(
        AgentReasoningEffort? reasoningEffort = null,
        float? temperature = null,
        int? maxOutputTokens = null,
        int? reasoningMaxTokens = null
    )
    {
        if (reasoningEffort is { } effort && !Enum.IsDefined(effort))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reasoningEffort),
                effort,
                "Reasoning effort must be None, Low, Medium or High."
            );
        }
        if (temperature is { } value && (!float.IsFinite(value) || value is < 0 or > 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(temperature),
                value,
                "Temperature must be a finite number from 0 to 2."
            );
        }
        if (maxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens),
                maxOutputTokens,
                "Max output tokens must be positive."
            );
        }
        if (reasoningMaxTokens is < 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reasoningMaxTokens),
                reasoningMaxTokens,
                "Reasoning max tokens must be at least 1024."
            );
        }
        if (reasoningEffort is not null && reasoningMaxTokens is not null)
        {
            throw new ArgumentException(
                "Reasoning effort and reasoning max tokens are mutually exclusive."
            );
        }

        ReasoningEffort = reasoningEffort;
        ReasoningMaxTokens = reasoningMaxTokens;
        Temperature = temperature;
        MaxOutputTokens = maxOutputTokens;
    }

    public AgentReasoningEffort? ReasoningEffort { get; }
    public int? ReasoningMaxTokens { get; }
    public float? Temperature { get; }
    public int? MaxOutputTokens { get; }
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
    private AgentWorkspaceDescriptor<TState>? _workspace;
    private AgentStructuredOutputDescriptor<TState>? _structuredOutput;
    private AgentCheckpointDescriptor<TState>? _checkpoint;
    private AgentContextBudgetDescriptor? _contextBudget;
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
        ToolEffect,
        JsonElement,
        CancellationToken,
        ValueTask<string?>
    >? _toolInterceptor;
    private AgentModelRequestOptions? _modelRequestOptions;
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
        ArgumentNullException.ThrowIfNull(instructions);
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
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                $"An agent timeout must be positive and at most {_maximumTimeout.TotalMilliseconds} milliseconds."
            );
        }
        _timeout = timeout;
        return this;
    }

    public AgentBuilder<TState> WithModelRequestOptions(AgentModelRequestOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _modelRequestOptions = options;
        return this;
    }

    internal AgentBuilder<TState> ConfigureImplementation(AgentImplementationFactory factory)
    {
        _implementationFactory = factory;
        return this;
    }

    internal AgentBuilder<TState> ConfigureWorkspace(
        AgentWorkspaceDescriptor<TState> workspace,
        Func<
            PipelineMessage<TState>,
            string,
            ToolEffect,
            JsonElement,
            CancellationToken,
            ValueTask<string?>
        >? toolInterceptor = null
    )
    {
        _workspace = workspace;
        _toolInterceptor = toolInterceptor;
        return this;
    }

    internal AgentBuilder<TState> ConfigureStructuredOutput(
        AgentStructuredOutputDescriptor<TState> descriptor
    )
    {
        _structuredOutput = descriptor;
        return this;
    }

    public AgentBuilder<TState> WithOutput<TOutput>(
        IAgentOutputDefinition<TState, TOutput> output,
        Func<TState, TOutput, TState> apply
    )
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(apply);
        var jsonSchema = StructuredOutputSchema.CreateJsonSchema<TOutput>();
        _structuredOutput = new AgentStructuredOutputDescriptor<TState, TOutput>(
            (response, state) =>
                AgentStructuredOutputPolicy.Parse(
                    response,
                    output.Validator,
                    output.ValidatorFor(state)
                ),
            apply,
            typeof(TOutput).FullName ?? typeof(TOutput).Name,
            output.Instructions,
            jsonSchema,
            ChatResponseFormat.ForJsonSchema(jsonSchema, typeof(TOutput).Name)
        )
        {
            ExampleFactory = state =>
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
                            JsonSerializer.Serialize(example.Output, TandemJson.TypedContract)
                        );
                    })
                    .ToArray(),
        };
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
        var jsonSchema = CapabilityContract.RequireObjectRoot(
            output.JsonSchema,
            "Output",
            nameof(output)
        );

        _structuredOutput = new AgentStructuredOutputDescriptor<TState, JsonElement>(
            (response, state) => ParseJsonOutput(response, state, output),
            apply,
            output.ValueType,
            output.Instructions,
            jsonSchema,
            ChatResponseFormat.ForJsonSchema(jsonSchema)
        );
        return this;
    }

    private static ParsedOutput<JsonElement> ParseJsonOutput(
        string response,
        TState state,
        AgentJsonOutputDefinition<TState> output
    )
    {
        JsonElement candidate;
        try
        {
            candidate = AgentStructuredJsonExtractor.Extract(response);
        }
        catch (Exception exception) when (exception is InvalidOperationException or JsonException)
        {
            return ParsedOutput<JsonElement>.Invalid("$", exception.Message);
        }

        var problems = output.Validate(candidate);
        if (problems.Count == 0 && output.ValidateFor is not null)
        {
            problems = output.ValidateFor(state, candidate);
        }
        return problems.Count > 0
            ? ParsedOutput<JsonElement>.Invalid(problems)
            : ParsedOutput<JsonElement>.Valid(candidate);
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

    /// <summary>
    /// Adds an acceptance step to the configured output. Acceptances run in configuration order
    /// after validation; the first to return problems sends the output back for correction.
    /// </summary>
    internal AgentBuilder<TState> ConfigureOutputAcceptance<TOutput>(
        StructuredOutputAcceptance<TState, TOutput> acceptance
    )
    {
        if (_structuredOutput is null)
        {
            throw new InvalidOperationException(
                "Output acceptance requires typed output. Call WithOutput(...) first."
            );
        }
        if (_structuredOutput is not AgentStructuredOutputDescriptor<TState, TOutput> typed)
        {
            throw new InvalidOperationException(
                $"Output acceptance for '{typeof(TOutput).Name}' cannot decorate the configured output."
            );
        }
        var existing = typed.Accept;
        _structuredOutput = typed with
        {
            Accept = existing is null
                ? acceptance
                : async (attempt, output, cancellationToken) =>
                    await existing(attempt, output, cancellationToken) is { Count: > 0 } problems
                        ? problems
                        : await acceptance(attempt, output, cancellationToken),
        };
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

    internal AgentBuilder<TState> ConfigureContextBudget(
        int contextWindowTokens,
        int maxOutputTokens,
        bool disableCompaction
    )
    {
        _contextBudget = new(contextWindowTokens, maxOutputTokens, disableCompaction);
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
                ResetSessionAfterRelease: policy.ResetSessionAfterRelease
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
        if (_structuredOutput is not { JsonSchema: null })
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(_instructions);
        }
        if (_message is null)
        {
            throw new InvalidOperationException($"Agent '{_id}' must configure a user message.");
        }
        if (_workspace is not null && _implementationFactory is null)
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
            _workspace,
            _structuredOutput,
            _checkpoint,
            _messageAugmentations,
            _turnPolicy,
            _continueSession,
            _profilePolicy,
            _retainConversation,
            _implementationFactory,
            _timeout,
            _stateGuards,
            _latchedGates,
            _skills,
            _contextBudget,
            _modelRequestOptions
        );

        return new AgentDefinition<TState>(
            _id,
            id => new AgentOperation<TState>(
                new AgentBlock<TState>(
                    config with
                    {
                        StepId = id,
                    },
                    _chatClient,
                    _toolInterceptor,
                    _chatClientFactory
                )
            )
        );
    }
}
