using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Tandem.Domain;

namespace Tandem.Tests.Composition;

/// <summary>
/// Deterministic block substitutes for composition proofs. They return
/// prepared outcomes without invoking a model and record their invocations.
/// These are substitutes for block operations, not a fake workflow runtime.
/// </summary>
internal sealed class ScriptedOutcomeBlock : Executor<PipelineMessage, PipelineMessage>
{
    public ConcurrentQueue<PipelineMessage> ReceivedMessages { get; } = new();

    private readonly Func<PipelineContext, BlockOutcome> _outcomeFactory;

    public ScriptedOutcomeBlock(string blockId, Func<PipelineContext, BlockOutcome> outcomeFactory)
        : base(blockId)
    {
        _outcomeFactory = outcomeFactory;
    }

    public ScriptedOutcomeBlock(string blockId, BlockOutcome outcome)
        : this(blockId, _ => outcome) { }

    public ScriptedOutcomeBlock(string blockId, string outcomeKind)
        : this(
            blockId,
            ctx => new BlockOutcome(
                outcomeKind,
                blockId,
                outcomeKind,
                JsonSerializer.SerializeToElement(new { })
            )
        ) { }

    public override ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        ReceivedMessages.Enqueue(message);
        var outcome = _outcomeFactory(message.Context);
        return ValueTask.FromResult(new PipelineMessage(message.Context, outcome));
    }

    public int InvocationCount => ReceivedMessages.Count;
}

/// <summary>
/// Block that records invocations and returns an outcome derived from the
/// incoming context (e.g. for inspecting verification index progression).
/// </summary>
internal sealed class RecordingBlock : Executor<PipelineMessage, PipelineMessage>
{
    public ConcurrentQueue<PipelineMessage> ReceivedMessages { get; } = new();

    private readonly Func<PipelineContext, BlockOutcome> _outcomeFactory;

    public RecordingBlock(string blockId, Func<PipelineContext, BlockOutcome> outcomeFactory)
        : base(blockId)
    {
        _outcomeFactory = outcomeFactory;
    }

    public RecordingBlock(string blockId, string outcomeKind)
        : this(
            blockId,
            ctx => new BlockOutcome(
                outcomeKind,
                blockId,
                outcomeKind,
                JsonSerializer.SerializeToElement(new { })
            )
        ) { }

    public override ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        ReceivedMessages.Enqueue(message);
        var outcome = _outcomeFactory(message.Context);
        return ValueTask.FromResult(new PipelineMessage(message.Context, outcome));
    }

    public int InvocationCount => ReceivedMessages.Count;
}

/// <summary>
/// A verification block substitute that returns a canned pass/fail result
/// based on the current verification index, recording the order of commands.
/// </summary>
internal sealed class ScriptedVerificationBlock : Executor<PipelineMessage, PipelineMessage>
{
    public ConcurrentQueue<int> InvokedIndices { get; } = new();

    private readonly bool[] _results;
    private readonly Func<PipelineContext, BlockOutcome> _outcomeFactory;

    public ScriptedVerificationBlock(params bool[] results)
        : base(BlockIds.Verify)
    {
        _results = results;
        _outcomeFactory = ctx =>
        {
            var idx = ctx.VerificationIndex;
            InvokedIndices.Enqueue(idx);
            var passed = idx < _results.Length && _results[idx];
            var newIndex = passed ? idx + 1 : idx;
            var kind = passed ? OutcomeKinds.CommandPassed : OutcomeKinds.CommandFailed;
            return new BlockOutcome(
                kind,
                BlockIds.Verify,
                kind,
                JsonSerializer.SerializeToElement(new { index = idx })
            );
        };
    }

    public override ValueTask<PipelineMessage> HandleAsync(
        PipelineMessage message,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        var outcome = _outcomeFactory(message.Context);
        var idx = message.Context.VerificationIndex;
        var passed = idx < _results.Length && _results[idx];
        var newIndex = passed ? idx + 1 : idx;
        var updated = message.Context with
        {
            VerificationIndex = newIndex,
            VerificationResults = message
                .Context.VerificationResults.Append(
                    new VerificationResult(
                        idx,
                        message.Context.Packet.Verification[idx],
                        passed ? 0 : 1,
                        "",
                        "",
                        TimeSpan.Zero
                    )
                )
                .ToList(),
        };
        return ValueTask.FromResult(new PipelineMessage(updated, outcome));
    }
}

internal static class TestPackets
{
    public static Packet MakePacket(params string[] verificationCommands) =>
        new(
            Title: "Test packet",
            Repository: "file:///nonexistent",
            Base: "main",
            Outcomes: [new Outcome("o1", "Do the thing.")],
            Verification: verificationCommands,
            Constraints: [],
            ImplementationContext: ""
        );

    public static Packet MakePacketWithOutcomes(params Outcome[] outcomes) =>
        new(
            Title: "Test packet",
            Repository: "file:///nonexistent",
            Base: "main",
            Outcomes: outcomes,
            Verification: [],
            Constraints: [],
            ImplementationContext: ""
        );
}
