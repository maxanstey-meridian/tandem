using FluentAssertions;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using Microsoft.Extensions.AI;

namespace Tandem.Tests.Composition;

/// <summary>
/// Slice A characterization (PLAN.md) of the production
/// <see cref="DeliveryComposition"/> workflow as built by MAF's
/// <c>WorkflowBuilder</c>. Assertions target only the public MAF reflection
/// (<see cref="Workflow.ReflectExecutors"/>, <see cref="Workflow.ReflectPorts"/>,
/// <see cref="Workflow.ReflectEdges"/>) and visualization
/// (<see cref="WorkflowVisualizer.ToMermaidString"/> /
/// <see cref="WorkflowVisualizer.ToDotString"/>) surfaces and do not modify
/// production code. Slice D will extend this test to prove labelled edge
/// overloads leave the durable graph identity unchanged.
/// </summary>
public sealed class DeliveryCompositionGraphTests : IDisposable
{
    private const string HumanInputPortId = "HumanInput";

    private readonly string _tandemHome;
    private readonly Pipeline<DeliveryState> _pipeline;
    private readonly Workflow _workflow;

    public DeliveryCompositionGraphTests()
    {
        _tandemHome = Path.Combine(
            Path.GetTempPath(),
            "tandem-graph-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(_tandemHome);

        var composition = new DeliveryComposition(
            CreateFactory(
                new AgentFactory(),
                _ => new FakeChatClient(),
                _ => MakeProfile(),
                new DeliveryDiffAcquisition(new GitProcess()),
                new WorkspacePreparation(new GitProcess()),
                new GitProcess()
            )
        );
        _pipeline = composition.Build();
        _workflow = PipelineMafBridge.GetWorkflow(_pipeline);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tandemHome))
        {
            Directory.Delete(_tandemHome, recursive: true);
        }
    }

    [Fact]
    public void Workflow_Name_And_Start_ArePreserved()
    {
        _workflow.Name.Should().Be("delivery");
        _workflow.StartExecutorId.Should().Be(BlockIds.Prepare);
        _workflow
            .Description.Should()
            .Be(
                "Plan, implement, verify, and review a software change.",
                "Slice D adds the lifecycle description via WithDescription"
            );
    }

    [Fact]
    public void DeliveryParticipants_ExposeStateSafeTerminalNodes()
    {
        typeof(DeliveryParticipants)
            .GetProperty(nameof(DeliveryParticipants.CompleteRun))!
            .PropertyType.Should()
            .Be(typeof(IPipelineNode<DeliveryState>));
        typeof(DeliveryParticipants)
            .GetProperty(nameof(DeliveryParticipants.FailRun))!
            .PropertyType.Should()
            .Be(typeof(IPipelineNode<DeliveryState>));
        typeof(DeliveryParticipants)
            .Assembly.GetType("Tandem.Delivery.CompleteRunStage")
            .Should()
            .BeNull();
        typeof(DeliveryParticipants)
            .Assembly.GetType("Tandem.Delivery.FailRunStage")
            .Should()
            .BeNull();
    }

    [Fact]
    public void PublicInspection_ReflectsTheExecutableWorkflowSemantics()
    {
        var inspection = new DeliveryComposition(
            CreateFactory(
                new AgentFactory(),
                _ => new FakeChatClient(),
                _ => MakeProfile(),
                new DeliveryDiffAcquisition(new GitProcess()),
                new WorkspacePreparation(new GitProcess()),
                new GitProcess()
            )
        )
            .Build()
            .Inspect();

        inspection.Name.Should().Be("delivery");
        inspection.StartStepId.Should().Be(BlockIds.Prepare);
        inspection.OutputStepIds.Should().Equal(BlockIds.Complete, BlockIds.Failed);
        inspection.StepIds.Should().HaveCount(9);
        inspection.Routes.Should().HaveCount(24);
        inspection
            .Routes.Should()
            .Contain(route =>
                route.SourceId == BlockIds.Planner
                && route.TargetId == HumanInputPortId
                && route.Conditional
            );
        inspection
            .Routes.SelectMany(route => new[] { route.SourceId, route.TargetId })
            .Should()
            .BeSubsetOf(inspection.StepIds);
        inspection.Mermaid.Should().StartWith("flowchart").And.Contain(BlockIds.Prepare);
        inspection.Dot.Should().StartWith("digraph");
    }

    [Fact]
    public void EverySemanticRouteLabel_IsRetainedInInspection()
    {
        var labels = _pipeline.Inspect().Routes.Select(route => route.Label).ToArray();

        var expectedLabels = new[]
        {
            "workspace prepared",
            "workspace failed",
            "planner requested",
            "report submitted",
            "checkpoint written",
            "proceed / proceed with constraints",
            "needs human",
            "stop",
            "verification configured",
            "no verification configured",
            "commands remain",
            "verification complete",
            "command failed",
            "capture failed",
            "verification failed",
            "agent failed",
            "accepted",
            "changes requested",
            "answer for planner",
            "answer for reviewer",
            "unknown answer source",
        };

        foreach (var label in expectedLabels)
        {
            labels.Should().Contain(label);
        }
    }

    [Fact]
    public void ExecutorSet_MatchesProductionNodeSet()
    {
        var expected = new[]
        {
            BlockIds.Prepare,
            BlockIds.Executor,
            BlockIds.Planner,
            BlockIds.CaptureCandidate,
            BlockIds.Verify,
            BlockIds.Reviewer,
            BlockIds.Complete,
            BlockIds.Failed,
            BlockIds.HumanQuestion,
            HumanInputPortId,
            BlockIds.ApplyHumanAnswer,
        };

        var actual = _workflow.ReflectExecutors().Keys.OrderBy(k => k).ToArray();
        actual
            .Should()
            .Equal(
                expected.OrderBy(k => k).ToArray(),
                "the workflow must own exactly the production block set plus the HumanInput request port binding"
            );
    }

    [Fact]
    public void HumanInputInspection_ExposesAuthoredTypesWithoutPrivatePortTypes()
    {
        var inspection = _pipeline.Inspect();
        inspection
            .Interactions.Should()
            .ContainSingle()
            .Which.Should()
            .Be(
                new PipelineInteractionInspection(
                    HumanInputPortId,
                    typeof(HumanQuestion).FullName!,
                    typeof(HumanAnswer).FullName!
                )
            );
    }

    [Fact]
    public void Edges_MatchProductionGraph()
    {
        var got = FlattenEdgesSorted();
        var expected = ExpectedProductionEdges.ToList();
        expected.Sort(EdgeTupleComparer.Instance);

        got.Should()
            .Equal(
                expected,
                "the exact edge multiset (source, sink, has-condition) must match the production graph"
            );

        got.Count.Should()
            .Be(25, "MAF switch reflection collapses multiple cases that share one target");
    }

    [Fact]
    public void EdgeCountPerSource_MatchesProductionLayout()
    {
        var perSource = _workflow
            .ReflectExecutors()
            .Keys.Select(id => new
            {
                Id = id,
                Count = FlattenEdges().Count(edge => edge.Source == id),
            })
            .Where(entry => entry.Count > 0)
            .ToDictionary(entry => entry.Id, entry => entry.Count);

        perSource
            .Should()
            .Equal(
                new Dictionary<string, int>
                {
                    [BlockIds.Prepare] = 2,
                    [BlockIds.Executor] = 4,
                    [BlockIds.Planner] = 3,
                    [BlockIds.CaptureCandidate] = 3,
                    [BlockIds.Verify] = 4,
                    [BlockIds.Reviewer] = 4,
                    [BlockIds.HumanQuestion] = 1,
                    [HumanInputPortId] = 1,
                    [BlockIds.ApplyHumanAnswer] = 3,
                }
            );
    }

    [Fact]
    public void Planner_ToExecutor_IsExactlyOnePhysicalEdge()
    {
        var plannerToExecutor = FlattenEdges()
            .Where(e => e.Source == BlockIds.Planner && e.Sink == BlockIds.Executor)
            .ToList();

        plannerToExecutor
            .Should()
            .ContainSingle(
                "planner success (Proceed | ProceedWithConstraints) must remain one physical edge; "
                    + "the pinned durable adapter batches separate same-target edges"
            );
    }

    [Fact]
    public void Verification_Retains_SelfEdge()
    {
        FlattenEdges()
            .Should()
            .Contain(
                e => e.Source == BlockIds.Verify && e.Sink == BlockIds.Verify,
                "verification must keep a self-edge so a multi-command packet can iterate commands"
            );
    }

    [Fact]
    public void ApplyHumanAnswer_HasBoth_ReturnRoutes()
    {
        var fromApply = FlattenEdges().Where(e => e.Source == BlockIds.ApplyHumanAnswer).ToList();

        fromApply
            .Should()
            .Contain(
                e => e.Sink == BlockIds.Planner,
                "planner-originated human answers resume at the planner"
            );
        fromApply
            .Should()
            .Contain(
                e => e.Sink == BlockIds.Reviewer,
                "reviewer-originated human answers resume at the reviewer"
            );
    }

    [Fact]
    public void TerminalNodes_HaveNoOutgoingEdges()
    {
        var sourcesWithOutgoing = _workflow.ReflectEdges().Keys.ToHashSet();

        sourcesWithOutgoing
            .Should()
            .NotContain(BlockIds.Complete, "complete is terminal and emits no further edges");
        sourcesWithOutgoing
            .Should()
            .NotContain(BlockIds.Failed, "failed is terminal and emits no further edges");
    }

    [Fact]
    public void HumanQuestion_And_Port_Edges_Are_Unconditional()
    {
        var edges = FlattenEdges();

        edges
            .Should()
            .Contain(
                e =>
                    e.Source == BlockIds.HumanQuestion
                    && e.Sink == HumanInputPortId
                    && !e.HasCondition,
                "the question-to-request-port edge is unconditional and suspends the run"
            );
        edges
            .Should()
            .Contain(
                e =>
                    e.Source == HumanInputPortId
                    && e.Sink == BlockIds.ApplyHumanAnswer
                    && !e.HasCondition,
                "the answer delivery edge is unconditional"
            );
    }

    [Fact]
    public void EachEdgeEndpoint_ExistsInExecutorSet()
    {
        var executorIds = _workflow.ReflectExecutors().Keys.ToHashSet();
        var edgeTuples = FlattenEdges();

        edgeTuples
            .SelectMany(e => new[] { e.Source, e.Sink })
            .Distinct()
            .Should()
            .BeSubsetOf(
                executorIds,
                "every edge endpoint must resolve to a bound executor or request port"
            );
    }

    [Fact]
    public void MermaidOutput_RendersProductionGraph()
    {
        var mermaid = WorkflowVisualizer.ToMermaidString(_workflow);

        mermaid.Should().StartWith("flowchart");
        mermaid.Should().Contain("prepare", "the start node must appear");
        mermaid
            .Should()
            .Contain("(Start)", "MAF marks the start node in the Mermaid visualization");

        // Stable semantic node-id fragments only; full snapshot is intentionally
        // excluded (PLAN.md: framework formatting is not part of Tandem's contract).
        mermaid.Should().Contain(BlockIds.Planner);
        mermaid.Should().Contain(BlockIds.Executor);
        mermaid.Should().Contain(BlockIds.CaptureCandidate);
        mermaid.Should().Contain(BlockIds.Verify);
        mermaid.Should().Contain(BlockIds.Reviewer);
        mermaid.Should().Contain(BlockIds.Complete);
        mermaid.Should().Contain(BlockIds.Failed);
        mermaid.Should().Contain(BlockIds.HumanQuestion);
        mermaid.Should().Contain(HumanInputPortId);
        mermaid.Should().Contain(BlockIds.ApplyHumanAnswer);
    }

    [Fact]
    public void DotOutput_RendersProductionGraph()
    {
        var dot = WorkflowVisualizer.ToDotString(_workflow);

        dot.Should().StartWith("digraph");
        dot.Should().Contain(BlockIds.Prepare);
        dot.Should().Contain(BlockIds.Planner);
        dot.Should().Contain(BlockIds.HumanQuestion);
        dot.Should().Contain(HumanInputPortId);
    }

    private IReadOnlyList<EdgeTuple> FlattenEdges()
    {
        var list = new List<EdgeTuple>();
        foreach (var kvp in _workflow.ReflectEdges())
        {
            foreach (var info in kvp.Value)
            {
                var hasCondition = info is DirectEdgeInfo direct ? direct.HasCondition : true;
                foreach (var source in info.Connection.SourceIds)
                {
                    foreach (var sink in info.Connection.SinkIds)
                    {
                        list.Add(new EdgeTuple(source, sink, hasCondition));
                    }
                }
            }
        }

        return list;
    }

    private IReadOnlyList<EdgeTuple> FlattenEdgesSorted()
    {
        var list = FlattenEdges().ToList();
        list.Sort(EdgeTupleComparer.Instance);
        return list;
    }

    private sealed record EdgeTuple(string Source, string Sink, bool HasCondition);

    private sealed class EdgeTupleComparer : IComparer<EdgeTuple>
    {
        public static readonly EdgeTupleComparer Instance = new();

        public int Compare(EdgeTuple? x, EdgeTuple? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var c = string.CompareOrdinal(x.Source, y.Source);
            if (c != 0)
            {
                return c;
            }

            c = string.CompareOrdinal(x.Sink, y.Sink);
            if (c != 0)
            {
                return c;
            }

            return x.HasCondition.CompareTo(y.HasCondition);
        }
    }

    private static IReadOnlyList<EdgeTuple> ExpectedProductionEdges =>
        new List<EdgeTuple>
        {
            // prepare
            new(BlockIds.Prepare, BlockIds.Executor, true),
            new(BlockIds.Prepare, BlockIds.Failed, true),
            // executor
            new(BlockIds.Executor, BlockIds.Planner, true),
            new(BlockIds.Executor, BlockIds.CaptureCandidate, true),
            new(BlockIds.Executor, BlockIds.Executor, true),
            new(BlockIds.Executor, BlockIds.Failed, true),
            // Planner success outcomes (Proceed | ProceedWithConstraints) share one physical edge.
            new(BlockIds.Planner, BlockIds.Executor, true),
            new(BlockIds.Planner, BlockIds.HumanQuestion, true),
            // MAF switch reflection collapses distinct cases that share the failed target.
            new(BlockIds.Planner, BlockIds.Failed, true),
            // capture
            new(BlockIds.CaptureCandidate, BlockIds.Verify, true),
            new(BlockIds.CaptureCandidate, BlockIds.Reviewer, true),
            new(BlockIds.CaptureCandidate, BlockIds.Failed, true),
            // verify
            new(BlockIds.Verify, BlockIds.Verify, true),
            new(BlockIds.Verify, BlockIds.Reviewer, true),
            new(BlockIds.Verify, BlockIds.Executor, true),
            new(BlockIds.Verify, BlockIds.Failed, true),
            // reviewer
            new(BlockIds.Reviewer, BlockIds.Complete, true),
            new(BlockIds.Reviewer, BlockIds.Executor, true),
            new(BlockIds.Reviewer, BlockIds.HumanQuestion, true),
            new(BlockIds.Reviewer, BlockIds.Failed, true),
            // human suspension: question -> request port -> apply answer
            new(BlockIds.HumanQuestion, HumanInputPortId, false),
            new(HumanInputPortId, BlockIds.ApplyHumanAnswer, false),
            // apply-human-answer returns to the originating decision block.
            new(BlockIds.ApplyHumanAnswer, BlockIds.Planner, true),
            new(BlockIds.ApplyHumanAnswer, BlockIds.Reviewer, true),
            new(BlockIds.ApplyHumanAnswer, BlockIds.Failed, true),
        };

    private static DeliveryAgentProfile MakeProfile() => new(200000, 32000, 80);

    private static DeliveryParticipantsFactory CreateFactory(
        AgentFactory runtime,
        Func<string, IChatClient> clients,
        Func<string, DeliveryAgentProfile> profiles,
        DeliveryDiffAcquisition diff,
        WorkspacePreparation workspace,
        GitProcess git
    )
    {
        var capabilities = TestDeliveryCapabilities.Create();
        return new DeliveryParticipantsFactory(
            runtime,
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
        ) =>
            throw new InvalidOperationException(
                "FakeChatClient must not be invoked during graph construction."
            );

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
}
