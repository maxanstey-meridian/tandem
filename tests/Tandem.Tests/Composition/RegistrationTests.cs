using System.Collections;
using System.Reflection;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Tandem.Tests.Composition;

public sealed class RegistrationTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(),
        "tandem-registration-" + Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void AgentTimeout_RejectsUnsupportedDurations()
    {
        var builder = Agent
            .Create<TestState>("agent", "Respond.", new FakeChatClient())
            .WithMessage(state => state.Message);

        var tooLong = () => builder.WithTimeout(TimeSpan.MaxValue);

        tooLong.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void PublicRegistrations_ResolveDelivery()
    {
        Directory.CreateDirectory(_home);
        var services = new ServiceCollection();
        var clients = new FakeChatClients();
        services.AddDelivery(new DeliveryOptions(clients.Build, clients.ResolveProfile));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );

        provider.GetRequiredService<DeliveryComposition>().Should().NotBeNull();
    }

    [Fact]
    public void Agent_DirectClientBuildsWithDefaultFreshSession()
    {
        var definition = Agent
            .Create<TestState>("classify", "Classify the ticket.", new FakeChatClient())
            .WithMessage(state => state.Message)
            .Build();

        definition.Should().NotBeNull();
    }

    [Fact]
    public void Agent_BuildsWithoutWorkspaceCapability()
    {
        var operation = Agent
            .Create<TestState>("classify", "Classify the ticket.", new FakeChatClient())
            .WithMessage(state => state.Message)
            .Build();

        operation.Should().NotBeNull();
    }

    [Fact]
    public async Task AdvancedProfilePolicy_SelectsClientBeforeTheGovernedInvocation()
    {
        var primary = new RecordingModelClient();
        var promoted = new RecordingModelClient();
        IChatClient Resolve(string profile) => profile == "promoted" ? promoted : primary;
        var agent = AgentProfiles
            .Create<TestState>("profiled", "primary", "Respond once.", primary, Resolve)
            .WithMessage(state => state.Message)
            .WithProfilePolicy(state => new AgentProfileDecision(
                state.Promote ? "promoted" : "primary",
                "Test profile selection."
            ))
            .Build();
        var complete = PipelineNodes.Complete<TestState>("complete");
        var pipeline = Pipeline
            .Start(agent, "profile-selection")
            .Route(agent.Success, complete, "complete")
            .Build(complete);

        await new PipelineRunner().RunAsync(pipeline, new TestState("primary", Promote: false));
        await new PipelineRunner().RunAsync(pipeline, new TestState("promoted", Promote: true));

        primary.CallCount.Should().Be(1);
        promoted.CallCount.Should().Be(1);
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
        var builder = Agent
            .Create<FirstScope.SharedState>("agent", "Test capabilities.", new FakeChatClient())
            .WithMessage(_ => "message")
            .WithCapability(first)
            .WithCapability(first);

        var capabilities = typeof(AgentBuilder<FirstScope.SharedState>)
            .GetField("_capabilities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(builder)
            .Should()
            .BeAssignableTo<IReadOnlyList<AgentCapabilityDescriptor<FirstScope.SharedState>>>()
            .Subject;
        capabilities.Should().ContainSingle().Which.Should().BeSameAs(first.Descriptor);
    }

    [Fact]
    public void DefaultAgentDefinition_IsDirectlyComposableWithTypedOutcomeSelectors()
    {
        var definition = Agent
            .Create<TestState>("classify", "Classify the ticket.", new FakeChatClient())
            .WithMessage(state => state.Message)
            .Build();
        var complete = PipelineNodes.Complete<TestState>("complete");
        var failed = PipelineNodes.Failed<TestState>("failed");

        var inspection = Pipeline
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
    public async Task ConcurrentBuilds_CaptureNoRunObserver()
    {
        Directory.CreateDirectory(_home);
        var clients = new FakeChatClients();
        var services = new ServiceCollection();
        services.AddDelivery(new DeliveryOptions(clients.Build, clients.ResolveProfile));

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true }
        );
        var composition = provider.GetRequiredService<DeliveryComposition>();
        var firstObserver = new RecordingObserver();
        var secondObserver = new RecordingObserver();

        var builds = await Task.WhenAll(Task.Run(composition.Build), Task.Run(composition.Build));

        ContainsReference(builds[0], firstObserver).Should().BeFalse();
        ContainsReference(builds[0], secondObserver).Should().BeFalse();
        ContainsReference(builds[1], firstObserver).Should().BeFalse();
        ContainsReference(builds[1], secondObserver).Should().BeFalse();
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

    private sealed class FakeChatClients
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

        public DeliveryAgentProfile ResolveProfile(string _) => new(1000, 100, 80);
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

    private sealed class RecordingModelClient : IChatClient
    {
        public int CallCount { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation]
                CancellationToken cancellationToken = default
        )
        {
            CallCount++;
            var response = new ChatResponse(
                new ChatMessage(ChatRole.Assistant, [new TextContent("done")])
            )
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "test-model",
            };
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed record TestState(string Message, bool Promote = false);

    private sealed record IncrementRequest(int Amount);

    private static class FirstScope
    {
        public sealed record SharedState(int Count);
    }

    private sealed class RecordingObserver : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
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
