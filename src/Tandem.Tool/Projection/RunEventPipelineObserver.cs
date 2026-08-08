using System.Collections.Concurrent;
using Tandem.Delivery;

namespace Tandem.Infrastructure.Projection;

public sealed class RunEventPipelineObserver(
    Func<string, RunEventProjector> projectorFactory,
    Action<AgentUpdate>? onAgentUpdate = null
) : IPipelineObserver
{
    private readonly ConcurrentDictionary<string, string> _interactionSources = new(
        StringComparer.Ordinal
    );

    public async ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    )
    {
        var projector = projectorFactory(observation.StepId);
        switch (observation)
        {
            case PipelineStepStarted:
                await projector.EmitBlockStartedAsync(cancellationToken);
                break;
            case PipelineStepCompleted completed:
                await projector.EmitBlockCompletedAsync(completed.Outcome, cancellationToken);
                break;
            case PipelineStepFaulted faulted:
                await projector.EmitBlockCompletedAsync(
                    new PipelineRunOutcome(
                        "block.faulted",
                        faulted.StepId,
                        faulted.Error,
                        default,
                        default
                    ),
                    CancellationToken.None
                );
                break;
            case PipelineStepCancelled cancelled:
                await projector.EmitBlockCompletedAsync(
                    new PipelineRunOutcome(
                        "block.cancelled",
                        cancelled.StepId,
                        "Cancelled",
                        default,
                        default
                    ),
                    CancellationToken.None
                );
                break;
            case PipelineAgentUpdated updated:
                onAgentUpdate?.Invoke(updated.Update);
                await projector.EmitAgentUpdateAsync(updated.Update, cancellationToken);
                break;
            case PipelineCommandOutput command:
                await projector.EmitCommandOutputAsync(
                    command.Command,
                    command.Output,
                    command.ExitCode,
                    cancellationToken
                );
                break;
            case PipelineInteractionRequested<HumanQuestion> requested:
                _interactionSources[requested.RequestId] = requested.StepId;
                await projector.EmitHumanRequestedAsync(
                    requested.StepId,
                    requested.Request,
                    cancellationToken
                );
                break;
            case PipelineInteractionAnswered<HumanAnswer> answered:
                var source = _interactionSources.TryRemove(
                    answered.RequestId,
                    out var interactionId
                )
                    ? interactionId
                    : "unknown";
                await projector.EmitHumanAnsweredAsync(
                    source,
                    answered.Response.Text,
                    cancellationToken
                );
                break;
        }
    }
}
