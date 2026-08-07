using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Tandem.Actions;

public sealed class LifecycleMcpHost
{
    public static async Task RunAsync(
        LifecycleActionSetRegistry actionSets,
        string actionSetIdentity,
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

        actionSets.Register(actionSetIdentity, builder.Services).WithStdioServerTransport();

        builder.Services.AddSingleton(new LifecycleReceiptStore(tandemHome));
        builder.Services.AddSingleton(new LifecycleToolContext(runId, blockId, invocationId));

        using var host = builder.Build();
        await host.RunAsync(cancellationToken);
    }
}

public sealed record LifecycleToolContext(Guid RunId, string BlockId, string InvocationId);

public sealed record LifecycleActionSetRegistration(
    string Identity,
    Func<IServiceCollection, IMcpServerBuilder> Register
);

public sealed class LifecycleActionSetRegistry
{
    private readonly IReadOnlyDictionary<string, LifecycleActionSetRegistration> _registrations;

    public LifecycleActionSetRegistry(params LifecycleActionSetRegistration[] registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Any(registration => string.IsNullOrWhiteSpace(registration.Identity)))
        {
            throw new ArgumentException(
                "Lifecycle action set identities must not be blank.",
                nameof(registrations)
            );
        }
        _registrations = registrations.ToDictionary(
            registration => registration.Identity,
            StringComparer.Ordinal
        );
    }

    public IMcpServerBuilder Register(string identity, IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!_registrations.TryGetValue(identity, out var registration))
        {
            throw new InvalidOperationException(
                $"Lifecycle action set '{identity}' is not registered."
            );
        }

        return registration.Register(services);
    }
}
