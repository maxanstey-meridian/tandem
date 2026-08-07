using System.Text.Json;
using Microsoft.Extensions.AI;
using Tandem.Domain;

namespace Tandem.Advanced;

public sealed record OperationResult<TState>(TState State, OperationOutcome Outcome)
{
    internal static OperationResult<TState> From(PipelineMessage<TState> message)
    {
        var outcome =
            message.LatestOutcome
            ?? throw new InvalidOperationException("Operation produced no outcome.");
        return new OperationResult<TState>(
            message.State,
            new OperationOutcome(outcome.Kind, outcome.Summary, outcome.Payload)
        );
    }
}

public sealed record OperationOutcome(string Kind, string Summary, JsonElement Payload);

public static class AdvancedAgentBuilderExtensions
{
    public static AgentBuilder<TState> WithMessageFromContext<TState>(
        this AgentBuilder<TState> builder,
        AdvancedAgentMessage<TState> message
    ) => builder.ConfigureMessageFromContext(message);

    public static AgentBuilder<TState> WithWorkspace<TState>(
        this AgentBuilder<TState> builder,
        Func<TState, string> path,
        Func<TState, bool> allowMutation,
        ToolInterceptor<TState>? toolInterceptor = null
    ) => builder.ConfigureWorkspace(path, allowMutation, toolInterceptor);

    public static AgentBuilder<TState> WithStructuredOutput<TState>(
        this AgentBuilder<TState> builder,
        StructuredOutputParser<TState> parser,
        Action<ChatOptions>? configureChatOptions = null,
        StructuredOutputAcceptancePolicy<TState>? acceptancePolicy = null,
        string? correctionRequiredToolName = null
    ) =>
        builder.ConfigureStructuredOutput(
            parser,
            configureChatOptions,
            acceptancePolicy,
            correctionRequiredToolName
        );

    public static AgentBuilder<TState> WithOutput<TState, TOutput>(
        this AgentBuilder<TState> builder,
        StructuredOutputParser<TState> parser,
        StructuredOutputAcceptancePolicy<TState>? acceptancePolicy = null,
        string? correctionRequiredToolName = null
    ) => builder.ConfigureOutput<TOutput>(parser, acceptancePolicy, correctionRequiredToolName);

    public static AgentBuilder<TState> WithCapability<TState>(
        this AgentBuilder<TState> builder,
        AgentCapability<TState> capability
    ) => builder.ConfigureCapability(capability);

    public static AgentBuilder<TState> WithCheckpoint<TState>(
        this AgentBuilder<TState> builder,
        CheckpointPolicy<TState> policy
    ) => builder.ConfigureCheckpoint(policy);

    public static AgentBuilder<TState> WithMessageAugmentation<TState>(
        this AgentBuilder<TState> builder,
        MessageAugmentation<TState> augmentation
    ) => builder.ConfigureMessageAugmentation(augmentation);

    public static AgentBuilder<TState> WithContinuationPolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentTurnPolicy<TState> policy
    ) => builder.ConfigureContinuationPolicy(policy);

    public static AgentBuilder<TState> WithProfilePolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentProfilePolicy<TState> policy
    ) => builder.ConfigureProfilePolicy(policy);

    public static AgentBuilder<TState> WithConversationPolicy<TState>(
        this AgentBuilder<TState> builder,
        AgentConversationPolicy<TState> policy
    ) => builder.ConfigureConversationPolicy(policy);
}

public interface IPipelineExecutionContext
{
    public ValueTask QueueStateUpdateAsync(
        string key,
        string value,
        string scopeName,
        CancellationToken cancellationToken
    );

    public ValueTask<HashSet<string>> ReadStateKeysAsync(
        string scopeName,
        CancellationToken cancellationToken
    );

    public ValueTask<T?> ReadStateAsync<T>(
        string key,
        string scopeName,
        CancellationToken cancellationToken
    );
}

internal static class AdvancedPipelineNodes
{
    public static PipelineNodeDescriptor Stage<TInput, TOutput>(
        string id,
        Func<TInput, IPipelineExecutionContext, CancellationToken, ValueTask<TOutput>> execute,
        IBlockExecutionObserver? observer = null
    ) => new DelegatePipelineNodeDescriptor<TInput, TOutput>(id, execute, observer);

    public static PipelineNodeDescriptor RequestPort<TRequest, TResponse>(string id) =>
        new RequestPortPipelineNodeDescriptor<TRequest, TResponse>(id);
}

public static class PipelineOperation
{
    public static async ValueTask<Outcome<TState>> RunOutcomeAsync<TState>(
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

    public static async ValueTask<TState> RunStateAsync<TState>(
        TState state,
        Func<PipelineMessage<TState>, ValueTask<PipelineMessage<TState>>> execute
    )
    {
        using var operation = PipelineExecutionEnvelope.BeginOperation<TState>();
        var result = await execute(PipelineExecutionEnvelope.Get(state));
        PipelineExecutionEnvelope.Set(result);
        return result.State;
    }

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
