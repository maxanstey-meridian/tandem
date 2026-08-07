using System.Collections;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Actions;
using Tandem.Domain;

namespace Tandem.Tests.Composition;

public sealed class RegistrationTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(),
        "tandem-registration-" + Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void PublicRegistrations_ResolveDeliveryAndItsExplicitActionSet()
    {
        Directory.CreateDirectory(_home);
        var services = new ServiceCollection();
        services.AddSingleton<ITandemChatClients>(new FakeChatClients());
        services.AddTandem().AddDelivery();
        services.AddSingleton(new TandemEnvironment(_home, "unused"));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );

        provider.GetRequiredService<DeliveryComposition>().Should().NotBeNull();
        provider
            .GetRequiredService<LifecycleActionSetRegistry>()
            .Should()
            .NotBeNull("Delivery registers its lifecycle action set through AddDelivery");
    }

    [Fact]
    public void AgentRuntime_RequiresExplicitSessionPolicyBeforeBuild()
    {
        var builder = new AgentRuntime(_home, null)
            .Create<TestState>("classify", "support", "Classify the ticket.", new FakeChatClient())
            .WithMessage(state => state.Message);

        var act = () => builder.Build();

        act.Should().Throw<InvalidOperationException>().WithMessage("*session policy*");
    }

    [Fact]
    public void AgentRuntime_BuildsWithoutWorkspaceCapability()
    {
        var operation = new AgentRuntime(_home, null)
            .Create<TestState>("classify", "support", "Classify the ticket.", new FakeChatClient())
            .WithMessage(state => state.Message)
            .WithSessionPolicy(_ => new AgentSessionDecision(
                AgentSessionAction.Reset,
                "Classify independently."
            ))
            .Build();

        operation.Should().NotBeNull();
    }

    [Fact]
    public async Task ConcurrentBuilds_FromOneDiRoot_IsolateContextsAndReuseStableDependencies()
    {
        Directory.CreateDirectory(_home);
        var clients = new FakeChatClients();
        var services = new ServiceCollection();
        services.AddSingleton<ITandemChatClients>(clients);
        services.AddTandem().AddDelivery();
        services.AddSingleton(new TandemEnvironment(_home, "unused"));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
        var composition = provider.GetRequiredService<DeliveryComposition>();
        Action<string, Guid, AgentUpdate> firstUpdate = (_, _, _) => { };
        Action<string, Guid, AgentUpdate> secondUpdate = (_, _, _) => { };
        var firstObserver = new RecordingObserver();
        var secondObserver = new RecordingObserver();

        var builds = await Task.WhenAll(
            Task.Run(() => composition.Build(new PipelineBuildContext(firstUpdate, firstObserver))),
            Task.Run(() =>
                composition.Build(new PipelineBuildContext(secondUpdate, secondObserver))
            )
        );

        ContainsReference(builds[0], firstUpdate).Should().BeTrue();
        ContainsReference(builds[0], secondUpdate).Should().BeFalse();
        ContainsReference(builds[0], firstObserver).Should().BeTrue();
        ContainsReference(builds[0], secondObserver).Should().BeFalse();
        ContainsReference(builds[1], secondUpdate).Should().BeTrue();
        ContainsReference(builds[1], firstUpdate).Should().BeFalse();
        ContainsReference(builds[1], secondObserver).Should().BeTrue();
        ContainsReference(builds[1], firstObserver).Should().BeFalse();

        foreach (var client in clients.Instances)
        {
            ContainsReference(builds[0], client).Should().BeTrue();
            ContainsReference(builds[1], client).Should().BeTrue();
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    private sealed class FakeChatClients : ITandemChatClients
    {
        private readonly IReadOnlyDictionary<string, FakeChatClient> _instances = new Dictionary<
            string,
            FakeChatClient
        >
        {
            ["implementation"] = new(),
            ["planning"] = new(),
            ["review"] = new(),
        };

        public IEnumerable<FakeChatClient> Instances => _instances.Values;

        public IChatClient Build(string profileName) => _instances[profileName];

        public ResolvedProfile ResolveProfile(string profileName) =>
            new("test", "http://localhost", "test", WireApi.Completions, null, 1000, 100, 80);
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Registration must not invoke a model.");

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed record TestState(string Message);

    private sealed class RecordingObserver : IBlockExecutionObserver, ICommandOutputObserver
    {
        public ValueTask StartedAsync(string blockId, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask CompletedAsync<TInput, TOutput>(
            string blockId,
            TInput input,
            TOutput output,
            TimeSpan duration,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;

        public ValueTask CommandOutputAsync(
            string blockId,
            string command,
            string output,
            int exitCode,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }

    private static bool ContainsReference(object root, object expected)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        return Visit(root, 0);

        bool Visit(object? value, int depth)
        {
            if (value is null || depth > 12 || !visited.Add(value))
            {
                return false;
            }

            if (ReferenceEquals(value, expected))
            {
                return true;
            }

            var type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || value is string or Type)
            {
                return false;
            }

            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence)
                {
                    if (Visit(item, depth + 1))
                    {
                        return true;
                    }
                }
            }

            for (var current = type; current is not null; current = current.BaseType)
            {
                if (
                    current
                        .GetFields(
                            BindingFlags.Instance
                                | BindingFlags.Public
                                | BindingFlags.NonPublic
                                | BindingFlags.DeclaredOnly
                        )
                        .Any(field => Visit(field.GetValue(value), depth + 1))
                )
                {
                    return true;
                }
            }

            return false;
        }
    }
}
