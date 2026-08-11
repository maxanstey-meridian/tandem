using System.Text.Json;

namespace Tandem.NodeApiSpike;

internal sealed class RegisteredObservationObserver(
    CallbackDispatcher callbacks,
    string callbackReference
) : IPipelineObserver
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public async ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        var projected = Project(observation);
        if (projected is not null)
        {
            _ = await callbacks.InvokeAsync(
                callbackReference,
                "",
                JsonSerializer.Serialize(projected, _json),
                cancellationToken
            );
        }
    }

    internal static object? Project(PipelineObservation observation) =>
        observation switch
        {
            PipelineStepStarted value => new
            {
                version = 1,
                kind = "stepStarted",
                value.StepId,
            },
            PipelineStepCompleted value => new
            {
                version = 1,
                kind = "stepCompleted",
                value.StepId,
            },
            PipelineStepCancelled value => new
            {
                version = 1,
                kind = "stepCancelled",
                value.StepId,
            },
            PipelineStepFaulted value => new
            {
                version = 1,
                kind = "stepFaulted",
                value.StepId,
                value.Error,
            },
            PipelineAgentUpdated { Update: AgentUpdate.Text update } value => new
            {
                version = 1,
                kind = "agentText",
                value.StepId,
                text = update.Value,
            },
            PipelineAgentUpdated { Update: AgentUpdate.Reasoning update } value => new
            {
                version = 1,
                kind = "agentReasoning",
                value.StepId,
                text = update.Value,
            },
            PipelineAgentUsage value => new
            {
                version = 1,
                kind = "agentUsage",
                value.StepId,
                value.InputTokens,
                value.OutputTokens,
                value.CurrentContextTokens,
            },
            _ => null,
        };
}

internal static class RegisteredRunObserver
{
    public static IPipelineObserver? Compose(
        IPipelinePersistenceObserver? persistence,
        IPipelineObserver? live,
        IPipelineObserver? presentation
    )
    {
        var additional = new[] { live, presentation }.Where(value => value is not null).ToArray();
        if (persistence is not null)
        {
            return new PersistenceFirstObserver(persistence, additional!);
        }
        return additional.Length switch
        {
            0 => null,
            1 => additional[0],
            _ => new SequentialObserver(additional!),
        };
    }

    private sealed class SequentialObserver(IReadOnlyList<IPipelineObserver> observers)
        : IPipelineObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            foreach (var observer in observers)
            {
                await observer.ObserveAsync(observation, cancellationToken);
            }
        }
    }

    private sealed class PersistenceFirstObserver(
        IPipelinePersistenceObserver persistence,
        IReadOnlyList<IPipelineObserver> additional
    ) : IPipelinePersistenceObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            await persistence.ObserveAsync(observation, cancellationToken);
            foreach (var observer in additional)
            {
                await observer.ObserveAsync(observation, cancellationToken);
            }
        }
    }
}
