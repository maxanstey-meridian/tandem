using System.Text.Json;

namespace Tandem.NodeApiSpike;

internal sealed class CallbackDispatcher(
    SynchronizationContext context,
    Func<string, string, string, string> invokeSync,
    Func<string, string, string, CancellationToken, Task<string>> invokeAsync,
    CancellationToken runCancellationToken
)
{
    public Task<string> InvokeAsync(
        string callback,
        string state,
        string input,
        CancellationToken cancellationToken
    ) =>
        InvokeResultAsync(
            NodePipelineBridge.InvokeOnJavaScriptThreadAsync(
                context,
                () => invokeAsync(callback, state, input, cancellationToken)
            )
        );

    public string Invoke(string callback, string state, string input)
    {
        if (ReferenceEquals(SynchronizationContext.Current, context))
        {
            return Parse(invokeSync(callback, state, input));
        }

        string? result = null;
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        context.Post(
            _ =>
            {
                try
                {
                    result = Parse(invokeSync(callback, state, input));
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    completed.Set();
                }
            },
            null
        );
        completed.Wait(runCancellationToken);
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return result!;
    }

    private static async Task<string> InvokeResultAsync(Task<string> result) => Parse(await result);

    private static string Parse(string result)
    {
        var envelope =
            JsonSerializer.Deserialize<CallbackResult>(result, JsonOptions)
            ?? throw new InvalidOperationException(
                "JavaScript callback returned no result envelope."
            );
        if (envelope.Succeeded)
            return envelope.Value
                ?? throw new InvalidOperationException("JavaScript callback returned no value.");
        var error =
            envelope.Error
            ?? throw new InvalidOperationException("JavaScript callback returned no error.");
        if (
            error.Name == "ContractValidationError"
            && error.Boundary is not null
            && error.Problems is not null
        )
            throw new CallbackContractException(error.Boundary, error.Problems);
        throw new InvalidOperationException($"JavaScript callback failed: {error.Message}");
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record CallbackResult(bool Succeeded, string? Value, CallbackError? Error);

    private sealed record CallbackError(
        string Name,
        string Message,
        string? Boundary,
        AgentJsonValidationProblem[]? Problems
    );
}

internal sealed class CallbackContractException(
    string boundary,
    IReadOnlyList<AgentJsonValidationProblem> problems
) : Exception("JavaScript callback contract validation failed.")
{
    public string Boundary { get; } = boundary;
    public IReadOnlyList<AgentJsonValidationProblem> Problems { get; } = problems;
}
