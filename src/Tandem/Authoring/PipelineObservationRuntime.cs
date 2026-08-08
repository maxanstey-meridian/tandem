using Tandem.Domain;

namespace Tandem;

public interface IPipelineObserver
{
    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    );
}

public abstract record PipelineObservation(Guid RunId, string StepId);

public sealed record PipelineStepStarted(Guid RunId, string StepId)
    : PipelineObservation(RunId, StepId);

public sealed record PipelineStepCompleted(Guid RunId, string StepId, PipelineRunOutcome Outcome)
    : PipelineObservation(RunId, StepId);

public sealed record PipelineStepFaulted(Guid RunId, string StepId, string Error)
    : PipelineObservation(RunId, StepId);

public sealed record PipelineStepCancelled(Guid RunId, string StepId)
    : PipelineObservation(RunId, StepId);

public sealed record PipelineAgentUpdated(Guid RunId, string StepId, AgentUpdate Update)
    : PipelineObservation(RunId, StepId);

public sealed record PipelineCommandOutput(
    Guid RunId,
    string StepId,
    string Command,
    string Output,
    int ExitCode
) : PipelineObservation(RunId, StepId);

public sealed record PipelineInteractionRequested<TRequest>(
    Guid RunId,
    string StepId,
    string RequestId,
    TRequest Request
) : PipelineObservation(RunId, StepId);

public sealed record PipelineInteractionAnswered<TResponse>(
    Guid RunId,
    string StepId,
    string RequestId,
    TResponse Response
) : PipelineObservation(RunId, StepId);

internal sealed class PipelineRunContext(Guid runId, IPipelineObserver? observer)
{
    public Guid RunId { get; } = runId;

    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    ) => observer?.ObserveAsync(observation, cancellationToken) ?? ValueTask.CompletedTask;
}

internal interface IPipelineRunContextCarrier
{
    public PipelineRunContext? RunContext { get; }
}

internal enum PipelineObservationMode
{
    Full,
    StartOnly,
    CompleteOnly,
    None,
}

internal static class PipelineObservationPublisher
{
    public static async ValueTask<TOutput> ExecuteAsync<TInput, TOutput>(
        string stepId,
        PipelineObservationMode mode,
        TInput input,
        Func<ValueTask<TOutput>> execute,
        CancellationToken cancellationToken
    )
    {
        var runContext = (input as IPipelineRunContextCarrier)?.RunContext;
        if (runContext is null || mode == PipelineObservationMode.None)
        {
            return await execute();
        }
        if (mode is PipelineObservationMode.Full or PipelineObservationMode.StartOnly)
        {
            await runContext.ObserveAsync(
                new PipelineStepStarted(runContext.RunId, stepId),
                cancellationToken
            );
        }
        var started = TimeProvider.System.GetTimestamp();
        try
        {
            var output = await execute();
            if (mode is PipelineObservationMode.Full or PipelineObservationMode.CompleteOnly)
            {
                await runContext.ObserveAsync(
                    new PipelineStepCompleted(
                        runContext.RunId,
                        stepId,
                        ToOutcome(stepId, output, TimeProvider.System.GetElapsedTime(started))
                    ),
                    cancellationToken
                );
            }
            return output;
        }
        catch (OperationCanceledException)
        {
            await runContext.ObserveAsync(
                new PipelineStepCancelled(runContext.RunId, stepId),
                CancellationToken.None
            );
            throw;
        }
        catch (Exception ex)
        {
            await runContext.ObserveAsync(
                new PipelineStepFaulted(runContext.RunId, stepId, ex.Message),
                CancellationToken.None
            );
            throw;
        }
    }

    private static PipelineRunOutcome ToOutcome<TOutput>(
        string stepId,
        TOutput output,
        TimeSpan duration
    )
    {
        var outcome = (output as IOutcomeBearingMessage)?.LatestOutcome;
        return outcome is null
            ? new PipelineRunOutcome("step.completed", stepId, "Completed", default, duration)
            : new PipelineRunOutcome(
                outcome.Kind,
                stepId,
                outcome.Summary,
                outcome.Payload,
                outcome.Duration == default ? duration : outcome.Duration
            );
    }
}
