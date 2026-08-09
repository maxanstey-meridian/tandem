namespace Tandem.NodeApiSpike;

internal sealed class CallbackDispatcher(
    SynchronizationContext context,
    Func<string, string, string, Task<string>> invoke
)
{
    public Task<string> InvokeAsync(string callback, string state, string input) =>
        NodePipelineBridge.InvokeOnJavaScriptThreadAsync(
            context,
            () => invoke(callback, state, input)
        );

    public string Invoke(string callback, string state, string input) =>
        InvokeAsync(callback, state, input).GetAwaiter().GetResult();
}
