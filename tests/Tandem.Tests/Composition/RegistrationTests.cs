using System.Reflection;
using FluentAssertions;
using FluentValidation;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Composition;

public sealed class RegistrationTests
{
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
        var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
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
        var first = AgentCapabilities.Create(
            new TestCapabilityDefinition<FirstScope.SharedState, IncrementRequest>(
                "increment",
                "Increment once.",
                validator,
                _ => "incremented"
            ),
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
        var complete = PipelineNodes.Complete(new TestCompletion<TestState>("complete"));
        var failed = PipelineNodes.Failed(new TestFailure<TestState>("failed"));

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
}
