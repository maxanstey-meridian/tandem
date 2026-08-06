using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tandem.Infrastructure.Lifecycle;

public sealed class LifecycleMcpHost
{
    public static async Task RunAsync(
        string tandemHome,
        Guid runId,
        string blockId,
        string invocationId,
        CancellationToken cancellationToken
    )
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.AddConsole(consoleLogOptions =>
        {
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Warning;
        });

        builder.Services.AddLifecycleMcpTools().WithStdioServerTransport();

        builder.Services.AddSingleton(new LifecycleReceiptStore(tandemHome));
        builder.Services.AddSingleton(new LifecycleToolContext(runId, blockId, invocationId));

        var host = builder.Build();
        await host.RunAsync(cancellationToken);
    }
}

public sealed record LifecycleToolContext(Guid RunId, string BlockId, string InvocationId);
