using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Tandem.Infrastructure;

namespace Tandem.Tests.Composition;

public sealed class DeliveryCompositionGraphTests
{
    private readonly DeliveryComposition _composition;
    private readonly Pipeline<DeliveryState> _pipeline;

    public DeliveryCompositionGraphTests()
    {
        _composition = new DeliveryComposition(
            CreateFactory(
                _ => new FakeChatClient(),
                _ => MakeProfile(),
                new DeliveryDiffAcquisition(new GitProcess()),
                new WorkspacePreparation(new GitProcess()),
                new GitProcess()
            )
        );
        _pipeline = _composition.Build();
    }

    [Fact]
    public void Inspection_ExposesTheSemanticDeliveryLifecycle()
    {
        var inspection = _pipeline.Inspect();

        inspection.Name.Should().Be("delivery");
        inspection.StartStepId.Should().Be(BlockIds.Prepare);
        inspection
            .StepIds.Should()
            .BeEquivalentTo([
                BlockIds.Prepare,
                BlockIds.Executor,
                BlockIds.Planner,
                BlockIds.CaptureCandidate,
                BlockIds.Verify,
                BlockIds.Reviewer,
                BlockIds.Complete,
                BlockIds.Failed,
                "PlannerHumanInput",
                "ReviewerHumanInput",
            ]);
        inspection
            .Interactions.Should()
            .BeEquivalentTo([
                new PipelineInteractionInspection(
                    "PlannerHumanInput",
                    typeof(HumanQuestion).FullName!,
                    typeof(HumanAnswer).FullName!
                ),
                new PipelineInteractionInspection(
                    "ReviewerHumanInput",
                    typeof(HumanQuestion).FullName!,
                    typeof(HumanAnswer).FullName!
                ),
            ]);
        inspection.Routes.Should().HaveCount(23);
        inspection
            .Routes.Should()
            .BeEquivalentTo(ExpectedRoutes, "composition is the complete Delivery lifecycle");
        inspection.OutputStepIds.Should().Equal(BlockIds.Complete, BlockIds.Failed);
        inspection.Mermaid.Should().NotContain("--request").And.NotContain("--resume");
        inspection.Dot.Should().NotContain("--request").And.NotContain("--resume");
    }

    [Fact]
    public void HumanInteractions_ReturnToTheirOwningAgentsWithoutStatePredicates()
    {
        var routes = _pipeline.Inspect().Routes;

        routes
            .Should()
            .ContainSingle(route =>
                route.SourceId == "PlannerHumanInput"
                && route.TargetId == BlockIds.Planner
                && !route.Conditional
            );
        routes
            .Should()
            .ContainSingle(route =>
                route.SourceId == "ReviewerHumanInput"
                && route.TargetId == BlockIds.Reviewer
                && !route.Conditional
            );
        routes.Should().NotContain(route => route.Label == "unknown answer source");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HumanInteractions_SuspendApplyAnswerAndResumeWithSemanticIdentity(
        bool planner
    )
    {
        var interaction = planner
            ? _composition.PlannerHumanInput
            : _composition.ReviewerHumanInput;
        var complete = PipelineNodes.Complete<DeliveryState>("interaction-complete");
        var pipeline = Pipeline
            .Start(interaction, planner ? "planner-interaction" : "reviewer-interaction")
            .Route(interaction, complete, "answered")
            .Build(complete);
        var initial = CreateHumanQuestionState(planner);
        PendingExternalRequest? request = null;
        var observations = new List<PipelineObservation>();
        var handler = new InlineExternalRequestHandler(pending =>
        {
            request = pending;
            return new ExternalRequestAnswer(
                pending.RunId,
                pending.RequestId,
                JsonSerializer.SerializeToElement(new HumanAnswer("Use the product decision."))
            );
        });
        var observer = new InlinePipelineObserver(observations.Add);

        var result = await new InProcessPipelineRunner().RunAsync(
            pipeline,
            Guid.CreateVersion7(),
            initial,
            handler,
            observer,
            CancellationToken.None
        );

        var expectedId = planner ? "PlannerHumanInput" : "ReviewerHumanInput";
        request.Should().NotBeNull();
        request!.PortId.Should().Be(expectedId);
        observations
            .OfType<PipelineInteractionRequested<HumanQuestion>>()
            .Should()
            .ContainSingle(observation => observation.StepId == expectedId);
        observations
            .OfType<PipelineInteractionAnswered<HumanAnswer>>()
            .Should()
            .ContainSingle(observation => observation.StepId == expectedId);
        result.Status.Should().Be(PipelineRunStatus.Succeeded);
        if (planner)
        {
            result.State.PlannerDecision.Should().BeNull();
            result.State.PlannerHumanAnswer.Should().Be("Use the product decision.");
            PlannerPrompts.BuildMessage(result.State).Should().Contain("Use the product decision.");
            result.State.ReviewerHumanAnswer.Should().BeNull();
        }
        else
        {
            result.State.ReviewerDecision.Should().BeNull();
            result.State.ReviewerHumanAnswer.Should().Be("Use the product decision.");
            ReviewerPrompts
                .BuildMessage(result.State)
                .Should()
                .Contain("Use the product decision.");
            result.State.PlannerHumanAnswer.Should().BeNull();
        }
    }

    private static IReadOnlyList<PipelineRouteInspection> ExpectedRoutes =>
        [
            new(BlockIds.Prepare, BlockIds.Executor, true, "workspace prepared"),
            new(BlockIds.Prepare, BlockIds.Failed, true, "workspace failed"),
            new(BlockIds.Executor, BlockIds.Planner, true, "planner requested"),
            new(BlockIds.Executor, BlockIds.CaptureCandidate, true, "report submitted"),
            new(BlockIds.Executor, BlockIds.Executor, true, "checkpoint written"),
            new(BlockIds.Executor, BlockIds.Failed, true, "agent failed"),
            new(BlockIds.Planner, BlockIds.Executor, true, "proceed / proceed with constraints"),
            new(BlockIds.Planner, "PlannerHumanInput", true, "needs human"),
            new(BlockIds.Planner, BlockIds.Failed, true, "stop"),
            new(BlockIds.Planner, BlockIds.Failed, true, "agent failed"),
            new(BlockIds.CaptureCandidate, BlockIds.Verify, true, "verification configured"),
            new(BlockIds.CaptureCandidate, BlockIds.Reviewer, true, "no verification configured"),
            new(BlockIds.CaptureCandidate, BlockIds.Failed, true, "capture failed"),
            new(BlockIds.Verify, BlockIds.Verify, true, "commands remain"),
            new(BlockIds.Verify, BlockIds.Reviewer, true, "verification complete"),
            new(BlockIds.Verify, BlockIds.Executor, true, "command failed"),
            new(BlockIds.Verify, BlockIds.Failed, true, "verification failed"),
            new(BlockIds.Reviewer, BlockIds.Complete, true, "accepted"),
            new(BlockIds.Reviewer, BlockIds.Executor, true, "changes requested"),
            new(BlockIds.Reviewer, "ReviewerHumanInput", true, "needs human"),
            new(BlockIds.Reviewer, BlockIds.Failed, true, "agent failed"),
            new("PlannerHumanInput", BlockIds.Planner, false, "answer for planner"),
            new("ReviewerHumanInput", BlockIds.Reviewer, false, "answer for reviewer"),
        ];

    private static DeliveryState CreateHumanQuestionState(bool planner)
    {
        var state = DeliveryState.Create(
            new Packet("test", "/tmp/repo", "main", [], [], [], ""),
            "base",
            "/tmp"
        );
        return planner
            ? state with
            {
                PlannerDecision = new PlannerDecision(
                    PlannerDecisionValue.NeedsHuman,
                    "A product decision is required.",
                    [],
                    [],
                    "Which behavior should be used?"
                ),
                Status = RunStatus.WaitingForHuman,
            }
            : state with
            {
                ReviewerDecision = new ReviewDecision(
                    ReviewDecisionValue.NeedsHuman,
                    "A product decision is required.",
                    [],
                    [],
                    "Which behavior should be used?"
                ),
                Status = RunStatus.WaitingForHuman,
            };
    }

    private static DeliveryAgentProfile MakeProfile() => new(200000, 32000, 80);

    private static DeliveryParticipantsFactory CreateFactory(
        Func<string, IChatClient> clients,
        Func<string, DeliveryAgentProfile> profiles,
        DeliveryDiffAcquisition diff,
        WorkspacePreparation workspace,
        GitProcess git
    )
    {
        var capabilities = TestDeliveryCapabilities.Create();
        return new DeliveryParticipantsFactory(
            clients,
            profiles,
            diff,
            workspace,
            git,
            capabilities.AskPlanner,
            capabilities.SubmitReport,
            capabilities.WriteCheckpoint
        );
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new InvalidOperationException("Fake client must not execute.");

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

    private sealed class InlineExternalRequestHandler(
        Func<PendingExternalRequest, ExternalRequestAnswer> answer
    ) : IExternalRequestHandler
    {
        public ValueTask<ExternalRequestAnswer> WaitAsync(
            PendingExternalRequest request,
            CancellationToken cancellationToken
        ) => ValueTask.FromResult(answer(request));
    }

    private sealed class InlinePipelineObserver(Action<PipelineObservation> observe)
        : IPipelineObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            observe(observation);
            return ValueTask.CompletedTask;
        }
    }
}
