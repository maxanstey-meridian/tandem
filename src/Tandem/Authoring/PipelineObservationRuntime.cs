using System.ComponentModel;
using System.Text.Json;
using Tandem.Domain;

namespace Tandem;

public interface IPipelineObserver
{
    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    );
}

[EditorBrowsable(EditorBrowsableState.Never)]
public interface IPipelinePersistenceObserver : IPipelineObserver;

public abstract record PipelineObservation(Guid RunId, string StepId);

public sealed record PipelineStepStarted(Guid RunId, string StepId)
    : PipelineObservation(RunId, StepId);

public sealed record PipelineStepCompleted(
    Guid RunId,
    string StepId,
    PipelineRunOutcome Outcome,
    PipelineAcceptedValue? AcceptedValue = null
) : PipelineObservation(RunId, StepId);

public sealed record PipelineAcceptedValue(string ValueType, JsonElement Payload)
{
    internal static PipelineAcceptedValue From<TValue>(TValue value)
    {
        var valueType = value?.GetType() ?? typeof(TValue);
        return new(
            valueType.FullName ?? valueType.Name,
            JsonSerializer.SerializeToElement(value, valueType, JsonSerializerOptions.Web)
        );
    }

    internal static PipelineAcceptedValue FromPayload<TValue>(JsonElement payload) =>
        new(typeof(TValue).FullName ?? typeof(TValue).Name, payload);
}

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

public abstract record PipelineInteractionRequestedObservation(
    Guid RunId,
    string StepId,
    string RequestId,
    string RequestType,
    JsonElement? Payload = null
) : PipelineObservation(RunId, StepId);

public sealed record PipelineInteractionRequested<TRequest>(
    Guid RunId,
    string StepId,
    string RequestId,
    TRequest Request,
    JsonElement? Payload = null
)
    : PipelineInteractionRequestedObservation(
        RunId,
        StepId,
        RequestId,
        typeof(TRequest).FullName ?? typeof(TRequest).Name,
        Payload
    );

public abstract record PipelineInteractionAnsweredObservation(
    Guid RunId,
    string StepId,
    string RequestId,
    string ResponseType,
    JsonElement? Payload = null
) : PipelineObservation(RunId, StepId);

public sealed record PipelineInteractionAnswered<TResponse>(
    Guid RunId,
    string StepId,
    string RequestId,
    TResponse Response,
    JsonElement? Payload = null
)
    : PipelineInteractionAnsweredObservation(
        RunId,
        StepId,
        RequestId,
        typeof(TResponse).FullName ?? typeof(TResponse).Name,
        Payload
    );

public sealed record PipelineAgentUsage(
    Guid RunId,
    string StepId,
    int InputTokens,
    int OutputTokens,
    int CurrentContextTokens
) : PipelineObservation(RunId, StepId);

public sealed record PipelineActionAttempted(
    Guid RunId,
    string StepId,
    string InvocationId,
    string ActionName,
    string Effect
) : PipelineObservation(RunId, StepId);

public sealed record PipelineActionCompleted(
    Guid RunId,
    string StepId,
    string InvocationId,
    string ActionName,
    string Effect,
    string Result
) : PipelineObservation(RunId, StepId);

public sealed record PipelineStructuredOutputAccepted(
    Guid RunId,
    string StepId,
    string AcceptedOutputId,
    string OutcomeKind,
    string? OutputType = null,
    JsonElement? Payload = null
) : PipelineObservation(RunId, StepId);

public sealed record PipelineCapabilityAccepted(
    Guid RunId,
    string StepId,
    string InvocationId,
    string CapabilityId,
    string CapabilityName,
    string AcceptedCallId,
    string? RequestType = null,
    JsonElement? Payload = null
) : PipelineObservation(RunId, StepId);

internal sealed class PipelineRunContext(
    Guid runId,
    IPipelineObserver? observer,
    IPipelineAcceptanceUnitOfWork? unitOfWork = null,
    IReadOnlySet<string>? persistentStepIds = null
)
{
    public Guid RunId { get; } = runId;

    public bool ShouldPersist(string stepId) => persistentStepIds?.Contains(stepId) is true;

    public ValueTask ObserveAsync(
        PipelineObservation observation,
        CancellationToken cancellationToken
    ) => observer?.ObserveAsync(observation, cancellationToken) ?? ValueTask.CompletedTask;

    public ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken
    ) => unitOfWork?.ExecuteAsync(operation, cancellationToken) ?? operation(cancellationToken);
}

internal interface IPipelineAcceptanceUnitOfWork
{
    public ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken
    );
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
        CancellationToken cancellationToken,
        Func<TOutput, PipelineAcceptedValue?>? acceptedValue = null
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
                var outcome = ToOutcome(
                    stepId,
                    output,
                    TimeProvider.System.GetElapsedTime(started)
                );
                PipelineAcceptedValue? accepted = null;
                if (runContext.ShouldPersist(stepId))
                {
                    accepted = acceptedValue?.Invoke(output);
                    if (accepted is null && outcome.Kind == StandardOutcomeKinds.Failed)
                    {
                        accepted = PipelineAcceptedValue.FromPayload<FailureEvidence>(
                            outcome.Payload
                        );
                    }
                }
                await runContext.ObserveAsync(
                    new PipelineStepCompleted(runContext.RunId, stepId, outcome, accepted),
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
