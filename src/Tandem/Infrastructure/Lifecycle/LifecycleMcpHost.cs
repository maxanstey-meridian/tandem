using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tandem.Infrastructure.Lifecycle;

public sealed class LifecycleMcpHost
{
    public static Task RunSimpleV1Async(
        string tandemHome,
        Guid runId,
        string blockId,
        string invocationId,
        CancellationToken cancellationToken
    ) =>
        RunAsync(
            tandemHome,
            runId,
            blockId,
            invocationId,
            services => services.AddSimpleV1McpTools(),
            cancellationToken
        );

    public static Task RunDebateAsync(
        string tandemHome,
        Guid runId,
        string blockId,
        string invocationId,
        CancellationToken cancellationToken
    ) =>
        RunAsync(
            tandemHome,
            runId,
            blockId,
            invocationId,
            services => services.AddDebateMcpTools(),
            cancellationToken
        );

    private static async Task RunAsync(
        string tandemHome,
        Guid runId,
        string blockId,
        string invocationId,
        Func<IServiceCollection, IMcpServerBuilder> registerTools,
        CancellationToken cancellationToken
    )
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.AddConsole(consoleLogOptions =>
        {
            consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Warning;
        });

        registerTools(builder.Services).WithStdioServerTransport();

        builder.Services.AddSingleton(new LifecycleReceiptStore(tandemHome));
        builder.Services.AddSingleton(new LifecycleToolContext(runId, blockId, invocationId));

        var host = builder.Build();
        await host.RunAsync(cancellationToken);
    }
}

public sealed record LifecycleToolContext(Guid RunId, string BlockId, string InvocationId);
