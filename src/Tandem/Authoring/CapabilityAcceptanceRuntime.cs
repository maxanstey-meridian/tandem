using System.Text.Json;
using Tandem.Infrastructure;

namespace Tandem;

internal static class CapabilityAcceptanceRuntime
{
    public static async ValueTask<object?> AcceptAsync<TState>(
        CapabilityInvocationState<TState> invocation,
        PipelineCapabilityAccepted accepted,
        Func<PipelineCapabilityAccepted, PipelineCapabilityAccepted> observation,
        Func<CancellationToken, ValueTask>? beforeAccept,
        Func<TState, TState> apply,
        CancellationToken cancellationToken
    )
    {
        if (!invocation.TryReserve())
        {
            return new ToolError(
                "conflicting_capability_outcome",
                "conflicting capability outcome",
                []
            );
        }

        var payload = accepted.Payload!.Value;
        var applying = false;
        try
        {
            async ValueTask<AcceptedCapability<TState>> AcceptCoreAsync(CancellationToken ct)
            {
                if (beforeAccept is not null)
                {
                    await beforeAccept(ct);
                }
                if (invocation.RunContext is { } runContext)
                {
                    await runContext.ObserveAsync(
                        observation(
                            runContext.ShouldPersist(invocation.StepId)
                                ? accepted
                                : accepted with
                                {
                                    Payload = null,
                                }
                        ),
                        ct
                    );
                }
                ct.ThrowIfCancellationRequested();
                applying = true;
                var acceptedState = apply(invocation.State);
                applying = false;
                return new AcceptedCapability<TState>(
                    accepted.CapabilityId,
                    accepted.CapabilityName,
                    acceptedState,
                    accepted.Summary,
                    payload
                );
            }

            var result = invocation.RunContext is { } executionContext
                ? await executionContext.ExecuteAsync(AcceptCoreAsync, cancellationToken)
                : await AcceptCoreAsync(cancellationToken);
            invocation.Commit(result);
            return JsonSerializer.SerializeToElement(
                new { accepted = true, outcome = new { kind = accepted.CapabilityId, payload } },
                TandemJson.TypedContract
            );
        }
        catch (OperationCanceledException)
        {
            invocation.Release();
            throw;
        }
        catch (Exception exception)
        {
            invocation.Release();
            if (applying)
            {
                invocation.RecordApplicationFault(exception);
                throw;
            }
            return new ToolError(
                "capability_acceptance_failed",
                "capability acceptance failed",
                [new ToolProblem(null, exception.Message)]
            );
        }
    }
}
