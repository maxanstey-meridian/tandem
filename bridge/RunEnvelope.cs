using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tandem.Bridge;

/// <summary>
/// How a registered run ended, returned to JavaScript for every expected outcome so that the
/// consumer never has to recover an outcome from exception text.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "status")]
[JsonDerivedType(typeof(RunSucceeded), "succeeded")]
[JsonDerivedType(typeof(RunFailed), "failed")]
[JsonDerivedType(typeof(RunCancelled), "cancelled")]
[JsonDerivedType(typeof(RunContractViolated), "contract")]
[JsonDerivedType(typeof(RunFaulted), "faulted")]
internal abstract record RunEnvelope(Guid RunId)
{
    private static readonly JsonSerializerOptions _options = TandemJson.CreateTypedContract();

    public string ToJson() => JsonSerializer.Serialize(this, _options);

    public static RunEnvelope Completed(PipelineRunResult<JavaScriptState> result)
    {
        var state = JsonElement.Parse(result.State.Json);
        var summary = result.Outcome?.Summary;
        return result.Succeeded
            ? new RunSucceeded(result.RunId, state, summary)
            : new RunFailed(result.RunId, state, summary);
    }

    /// <summary>
    /// Maps a run's failure. An operation cancelled without the run being cancelled, such as a
    /// timeout, is a fault.
    /// </summary>
    public static RunEnvelope Ended(Guid runId, Exception failure, bool runCancelled) =>
        Find<CallbackContractException>(failure) is { } callback
            ? new RunContractViolated(runId, callback.Boundary, callback.Problems)
        : failure is RegistrationContractException registration
            ? new RunContractViolated(runId, "registration contract", registration.Problems)
        : runCancelled && Find<OperationCanceledException>(failure) is { } cancellation
            ? new RunCancelled(runId, cancellation.Message)
        : new RunFaulted(
            runId,
            (
                failure is PipelineRunException { InnerException: { } inner } ? inner : failure
            ).ToString()
        );

    private static T? Find<T>(Exception exception)
        where T : Exception =>
        exception switch
        {
            T match => match,
            AggregateException aggregate => aggregate
                .InnerExceptions.Select(Find<T>)
                .FirstOrDefault(match => match is not null),
            { InnerException: { } inner } => Find<T>(inner),
            _ => null,
        };
}

internal sealed record RunSucceeded(Guid RunId, JsonElement State, string? Summary)
    : RunEnvelope(RunId);

internal sealed record RunFailed(Guid RunId, JsonElement State, string? Summary)
    : RunEnvelope(RunId);

internal sealed record RunCancelled(Guid RunId, string Message) : RunEnvelope(RunId);

internal sealed record RunContractViolated(
    Guid RunId,
    string Boundary,
    IReadOnlyList<ValidationProblem> Problems
) : RunEnvelope(RunId);

internal sealed record RunFaulted(Guid RunId, string Message) : RunEnvelope(RunId);
