using Microsoft.Agents.AI.Workflows;

namespace Tandem.Tests.Infrastructure;

internal sealed class NoOpWorkflowContext : IWorkflowContext
{
    public IReadOnlyDictionary<string, object?> State => new Dictionary<string, object?>();
    public string RunId => "test-run";
    public IReadOnlyDictionary<string, string> TraceContext => new Dictionary<string, string>();
    public bool ConcurrentRunsEnabled => false;

    public ValueTask AddEventAsync(
        WorkflowEvent workflowEvent,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask SendMessageAsync(
        object message,
        string? target,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask YieldOutputAsync(object output, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;

    public ValueTask RequestHaltAsync() => ValueTask.CompletedTask;

    public async ValueTask<T?> ReadStateAsync<T>(
        string key,
        string? scope,
        CancellationToken cancellationToken
    ) => default;

    public ValueTask<T> ReadOrInitStateAsync<T>(
        string key,
        Func<T> initializer,
        string? scope,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(initializer());

    public ValueTask<HashSet<string>> ReadStateKeysAsync(
        string? scope,
        CancellationToken cancellationToken
    ) => ValueTask.FromResult(new HashSet<string>());

    public ValueTask QueueStateUpdateAsync<T>(
        string key,
        T? value,
        string? scope,
        CancellationToken cancellationToken
    ) => ValueTask.CompletedTask;

    public ValueTask QueueClearScopeAsync(string? scope, CancellationToken cancellationToken) =>
        ValueTask.CompletedTask;
}
