using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Tandem.Sample.CodeWriter;
using CodeWriterReviewDecision = Tandem.Sample.CodeWriter.ReviewDecision;
using CodeWriterReviewDecisionValidator = Tandem.Sample.CodeWriter.ReviewDecisionValidator;
using CodeWriterVerificationResult = Tandem.Sample.CodeWriter.VerificationResult;

namespace Tandem.Tests.Composition;

public sealed class CodeWriterCompositionTests
{
    [Fact]
    public void RecordImplementation_ReplacesCandidateAndClearsStaleEvidence()
    {
        var state = new CodeWriterState(
            ["Return a URL slug."],
            new ImplementationCandidate("old", "old rationale"),
            new CodeWriterVerificationResult(true, [], null),
            new CodeWriterReviewDecision(ReviewDisposition.Accept, "Accepted", [])
        );

        var result = state.RecordImplementation(new SubmitImplementation("revised", "fixed"));

        result.Implementation.Should().Be(new ImplementationCandidate("revised", "fixed"));
        result.Verification.Should().BeNull();
        result.Review.Should().BeNull();
        result.Requirements.Should().Equal("Return a URL slug.");
    }

    [Fact]
    public void Validators_RejectIncompleteImplementationAndUnexplainedChangeRequest()
    {
        new SubmitImplementationValidator()
            .Validate(new SubmitImplementation("", ""))
            .IsValid.Should()
            .BeFalse();
        new CodeWriterReviewDecisionValidator()
            .Validate(
                new CodeWriterReviewDecision(ReviewDisposition.RequestChanges, "Needs work", [])
            )
            .IsValid.Should()
            .BeFalse();
        new CodeWriterReviewDecisionValidator()
            .Validate(new CodeWriterReviewDecision(ReviewDisposition.Accept, "Looks good", []))
            .IsValid.Should()
            .BeTrue();
    }

    [Fact]
    public void Composition_ExposesVerificationAndReviewLoops()
    {
        var pipeline = Build(ScriptedClients.Create());

        var inspection = pipeline.Inspect();

        inspection.Name.Should().Be("code-writer");
        inspection.StartStepId.Should().Be("implementer");
        inspection
            .StepIds.Should()
            .BeEquivalentTo("implementer", "verification", "reviewer", "done", "failed");
        inspection.OutputStepIds.Should().BeEquivalentTo("done", "failed");
        inspection.Routes.Should().HaveCount(7);
        inspection
            .Routes.Select(route => (route.SourceId, route.TargetId, route.Label))
            .Should()
            .Contain(("verification", "implementer", "verification failed"));
        inspection
            .Routes.Select(route => (route.SourceId, route.TargetId, route.Label))
            .Should()
            .Contain(("reviewer", "implementer", "changes requested"));
        inspection
            .Routes.Select(route => (route.SourceId, route.TargetId, route.Label))
            .Should()
            .Contain(("reviewer", "done", "accepted"));
    }

    [Fact]
    public async Task Assessment_CancellationStopsNonTerminatingCandidate()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var assessment = new ImplementationAssessment();

        var act = async () =>
            await assessment.AssessAsync("() => { while (true) {} }", cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CodeWriter_ExecutesVerificationAndReviewLoopsBeforeCompleting()
    {
        var clients = ScriptedClients.Create();
        var pipeline = Build(clients);

        var result = await new PipelineRunner().RunAsync(
            pipeline,
            new CodeWriterState(["Convert text to a lowercase ASCII URL slug."]),
            new PipelineRunOptions(Observer: new PersistenceObserver())
        );

        clients
            .Order.Should()
            .Equal("implementer", "implementer", "reviewer", "implementer", "reviewer");
        result.Status.Should().Be(PipelineRunStatus.Succeeded);
        result.State.Verification!.Passed.Should().BeTrue();
        result.State.Verification.Cases.Should().HaveCount(6);
        result.State.Verification.Cases.Should().OnlyContain(testCase => testCase.Passed);
        result
            .State.Review.Should()
            .BeEquivalentTo(
                new CodeWriterReviewDecision(
                    ReviewDisposition.Accept,
                    "Meets the requirements.",
                    []
                )
            );
        result.Outcome!.StepId.Should().Be("done");
    }

    private static Pipeline<CodeWriterState> Build(ScriptedClients clients)
    {
        var services = new ServiceCollection();
        services.AddCodeWriter(new CodeWriterClients(clients.Implementer, clients.Reviewer));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<CodeWriterComposition>().Build();
    }

    private sealed class ScriptedClients
    {
        private ScriptedClients(
            List<string> order,
            ScriptedChatClient implementer,
            ScriptedChatClient reviewer
        )
        {
            Order = order;
            Implementer = implementer;
            Reviewer = reviewer;
        }

        public List<string> Order { get; }
        public ScriptedChatClient Implementer { get; }
        public ScriptedChatClient Reviewer { get; }

        public static ScriptedClients Create()
        {
            var order = new List<string>();
            return new ScriptedClients(
                order,
                new ScriptedChatClient(
                    order,
                    "implementer",
                    CapabilityResponse("(input) => input", "Initial attempt."),
                    CapabilityResponse(SlugImplementation, "Handles all verification cases."),
                    CapabilityResponse(SlugImplementation, "Rechecked after review.")
                ),
                new ScriptedChatClient(
                    order,
                    "reviewer",
                    TextResponse(
                        "{\"decision\":\"RequestChanges\",\"summary\":\"Clarify the implementation.\",\"findings\":[\"Recheck normalization.\"]}"
                    ),
                    TextResponse(
                        "{\"decision\":\"Accept\",\"summary\":\"Meets the requirements.\",\"findings\":[]}"
                    )
                )
            );
        }

        private static ChatResponse CapabilityResponse(string implementation, string rationale) =>
            new(
                new ChatMessage(
                    ChatRole.Assistant,
                    [
                        new FunctionCallContent(
                            Guid.CreateVersion7().ToString("N"),
                            "submit_implementation",
                            new Dictionary<string, object?>
                            {
                                ["implementation"] = implementation,
                                ["rationale"] = rationale,
                            }
                        ),
                    ]
                )
            )
            {
                FinishReason = ChatFinishReason.ToolCalls,
                ModelId = "test-model",
            };

        private static ChatResponse TextResponse(string text) =>
            new(new ChatMessage(ChatRole.Assistant, [new TextContent(text)]))
            {
                FinishReason = ChatFinishReason.Stop,
                ModelId = "test-model",
            };

        private const string SlugImplementation =
            "(input) => input.normalize('NFD').replace(/[\\u0300-\\u036f]/g, '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '')";
    }

    private sealed class ScriptedChatClient(
        List<string> order,
        string name,
        params ChatResponse[] responses
    ) : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new(responses);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default
        ) => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            order.Add(name);
            var response = _responses.Dequeue();
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    private sealed class PersistenceObserver : IPipelinePersistenceObserver
    {
        public ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        ) => ValueTask.CompletedTask;
    }
}
