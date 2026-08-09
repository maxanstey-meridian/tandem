using System.Text.Json;

namespace Tandem;

internal static class CapabilityAcceptanceRuntime
{
    private static readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerOptions.Web);

    public static async ValueTask<object?> AcceptAsync<TState>(
        CapabilityInvocationState<TState> invocation,
        string capabilityId,
        string toolName,
        string requestType,
        JsonElement payload,
        string summary,
        Func<CancellationToken, ValueTask>? beforeAccept,
        Func<TState, TState> apply,
        CancellationToken cancellationToken
    )
    {
        if (!invocation.TryReserve())
        {
            return Error("conflicting capability outcome", []);
        }

        var applying = false;
        try
        {
            async ValueTask<AcceptedCapability<TState>> AcceptCoreAsync(CancellationToken ct)
            {
                if (beforeAccept is not null)
                {
                    await beforeAccept(ct);
                }
                if (invocation.RunContext is { } observedRunContext)
                {
                    await observedRunContext.ObserveAsync(
                        new PipelineCapabilityAccepted(
                            invocation.RunId,
                            invocation.StepId,
                            invocation.InvocationId,
                            capabilityId,
                            toolName,
                            $"{invocation.RunId:N}:{invocation.StepId}:{invocation.InvocationId}:{capabilityId}",
                            requestType,
                            observedRunContext.ShouldPersist(invocation.StepId) ? payload : null
                        ),
                        ct
                    );
                }
                ct.ThrowIfCancellationRequested();
                applying = true;
                var acceptedState = apply(invocation.State);
                applying = false;
                return new AcceptedCapability<TState>(
                    capabilityId,
                    toolName,
                    acceptedState,
                    summary,
                    payload
                );
            }

            var accepted = invocation.RunContext is { } runContext
                ? await runContext.ExecuteAsync(AcceptCoreAsync, cancellationToken)
                : await AcceptCoreAsync(cancellationToken);
            invocation.Commit(accepted);
            return JsonSerializer.SerializeToElement(
                new { accepted = true, outcome = new { kind = capabilityId, payload } },
                _jsonOptions
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
            return Error("capability acceptance failed", [exception.Message]);
        }
    }

    private static JsonElement Error(string error, IEnumerable<string> problems) =>
        JsonSerializer.SerializeToElement(
            new
            {
                isError = true,
                error,
                problems = problems.ToArray(),
            },
            _jsonOptions
        );
}
