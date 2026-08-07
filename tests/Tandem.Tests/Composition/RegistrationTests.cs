using System.Collections;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using FluentValidation;
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
    public void Capabilities_ApplyRepeatedAttachmentOnce()
    {
        var validator = new InlineValidator<IncrementRequest>();
        var first = AgentCapabilities.Create<FirstScope.SharedState, IncrementRequest>(
            "increment",
            "Increment once.",
            validator,
            _ => "incremented",
            (state, request) => state with { Count = state.Count + request.Amount }
        );
        var builder = new AgentRuntime(_home, null)
            .Create<FirstScope.SharedState>(
                "agent",
                "test",
                "Test capabilities.",
                new FakeChatClient()
            )
            .WithMessage(_ => "message")
            .WithSessionPolicy(_ => new AgentSessionDecision(
                AgentSessionAction.Reset,
                "Start fresh."
            ))
            .WithCapability(first)
            .WithCapability(first);

        var transition = typeof(AgentBuilder<FirstScope.SharedState>)
            .GetField("_receiptTransition", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(builder)
            .Should()
            .BeAssignableTo<ReceiptStateTransition<FirstScope.SharedState>>()
            .Subject;
        transition(
            new FirstScope.SharedState(0),
            first.ReceiptKind,
            JsonSerializer.SerializeToElement(new IncrementRequest(1))
        )
            .Count.Should()
            .Be(1);
    }

    [Fact]
    public void CapabilityRegistration_RejectsIdentityCollisionAcrossStateTypes()
    {
        var services = new ServiceCollection();
        var validator = new InlineValidator<IncrementRequest>();
        services.AddTandem();
        services.AddSingleton(
            AgentCapabilities.Create<FirstScope.SharedState, IncrementRequest>(
                "first",
                "First capability.",
                validator,
                _ => "first",
                (state, _) => state
            )
        );
        services.AddSingleton(
            AgentCapabilities.Create<SecondScope.SharedState, IncrementRequest>(
                "second",
                "Second capability.",
                validator,
                _ => "second",
                (state, _) => state
            )
        );
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<LifecycleActionSetRegistry>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*multiple state types*");
    }

    [Fact]
    public void CapabilityRegistration_RejectsDuplicateSemanticTool()
    {
        var services = new ServiceCollection();
        var validator = new InlineValidator<IncrementRequest>();
        services.AddTandem();
        services.AddSingleton(
            AgentCapabilities.Create<TestState, IncrementRequest>(
                "increment",
                "Increment once.",
                validator,
                _ => "incremented",
                (state, _) => state
            )
        );
        services.AddSingleton(
            AgentCapabilities.Create<TestState, IncrementRequest>(
                "increment",
                "Increment differently.",
                validator,
                _ => "incremented differently",
                (state, _) => state
            )
        );
        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<LifecycleActionSetRegistry>();

        act.Should().Throw<InvalidOperationException>().WithMessage("*registered more than once*");
    }

    [Fact]
    public void DefaultAgentDefinition_IsDirectlyComposableWithTypedOutcomeSelectors()
    {
        var definition = new AgentRuntime(_home, null)
            .Create<TestState>("classify", "support", "Classify the ticket.", new FakeChatClient())
            .WithMessage(state => state.Message)
            .WithSessionPolicy(_ => new AgentSessionDecision(
                AgentSessionAction.Reset,
                "Classify independently."
            ))
            .Build();
        var complete = PipelineNodes.Complete<TestState>("complete");
        var failed = PipelineNodes.Failed<TestState>("failed");

        var inspection = TandemWorkflow
            .Start(definition, "direct-agent-definition")
            .Route(definition.Success, complete, "classified")
            .Route(definition.Failed, failed, "classification failed")
            .Build(complete, failed)
            .Inspect();

        definition.Id.Should().Be("classify");
        definition.Should().BeAssignableTo<IGeneratedPipelineStep<TestState, Outcome<TestState>>>();
        inspection.StepIds.Should().BeEquivalentTo("classify", "complete", "failed");
        inspection.Routes.Should().HaveCount(2);
    }

    [Fact]
    public async Task ConcurrentBuilds_IsolateBlockObservers_AndRunUpdates()
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
        var firstRunId = Guid.CreateVersion7();
        var secondRunId = Guid.CreateVersion7();
        var firstObserver = new RecordingObserver();
        var secondObserver = new RecordingObserver();

        using var firstUpdates = AgentUpdates.Observe(firstRunId, firstUpdate);
        using var secondUpdates = AgentUpdates.Observe(secondRunId, secondUpdate);
        var builds = await Task.WhenAll(
            Task.Run(() =>
                composition.Build(new PipelineBuildContext(ExecutionObserver: firstObserver))
            ),
            Task.Run(() =>
                composition.Build(new PipelineBuildContext(ExecutionObserver: secondObserver))
            )
        );

        ContainsReference(builds[0], firstUpdate).Should().BeFalse();
        ContainsReference(builds[0], secondUpdate).Should().BeFalse();
        ContainsReference(builds[0], firstObserver).Should().BeTrue();
        ContainsReference(builds[0], secondObserver).Should().BeFalse();
        ContainsReference(builds[1], secondUpdate).Should().BeFalse();
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

    private sealed record IncrementRequest(int Amount);

    private static class FirstScope
    {
        public sealed record SharedState(int Count);
    }

    private static class SecondScope
    {
        public sealed record SharedState(int Count);
    }

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
