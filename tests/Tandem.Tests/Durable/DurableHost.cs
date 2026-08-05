using Microsoft.Agents.AI.DurableTask;
using Microsoft.Agents.AI.DurableTask.Workflows;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.AzureManaged;
using Microsoft.DurableTask.Worker.AzureManaged;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tandem.Tests.Durable;

/// <summary>
/// Builds and starts a Generic Host with MAF durable workflow services
/// configured against the local DTS emulator. Disposes cleanly.
/// </summary>
internal sealed class DurableHost : IAsyncDisposable
{
    private const string ConnectionString =
        "Endpoint="
        + DtsFixture.EmulatorAddress
        + ";TaskHub="
        + DtsFixture.TaskHub
        + ";Authentication=None";

    private readonly IHost _host;

    private DurableHost(IHost host)
    {
        _host = host;
    }

    public IServiceProvider Services => _host.Services;

    public IWorkflowClient WorkflowClient => _host.Services.GetRequiredService<IWorkflowClient>();

    public DurableTaskClient DurableTaskClient =>
        _host.Services.GetRequiredService<DurableTaskClient>();

    public static async Task<DurableHost> StartAsync(
        Action<DurableWorkflowOptions>? configureWorkflows = null,
        CancellationToken cancellationToken = default
    )
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.ConfigureDurableWorkflows(
                    options => configureWorkflows?.Invoke(options),
                    workerBuilder => workerBuilder.UseDurableTaskScheduler(ConnectionString),
                    clientBuilder => clientBuilder.UseDurableTaskScheduler(ConnectionString)
                );
            })
            .Build();

        await host.StartAsync(cancellationToken);
        return new DurableHost(host);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
