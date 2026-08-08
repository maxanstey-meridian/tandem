using Tandem.Delivery;

namespace Tandem.Tool;

internal sealed class TerminalHumanInteraction(IDeliveryRecordSink records)
{
    private readonly object _sync = new();
    private Pending? _pending;

    public async ValueTask<HumanAnswer> WaitAsync(
        PipelineInteractionContext<HumanQuestion, HumanAnswer> context,
        CancellationToken cancellationToken
    )
    {
        var pending = new Pending(context);
        lock (_sync)
        {
            if (_pending is not null)
            {
                throw new InvalidOperationException("A human interaction is already pending.");
            }
            _pending = pending;
        }

        try
        {
            return await pending.Completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pending, pending))
                {
                    _pending = null;
                }
            }
        }
    }

    public async Task SubmitAsync(Guid runId, string? answerText)
    {
        if (string.IsNullOrWhiteSpace(answerText))
        {
            return;
        }

        Pending pending;
        lock (_sync)
        {
            pending =
                _pending
                ?? throw new InvalidOperationException(
                    $"Run '{runId:N}' has no pending human interaction."
                );
            if (pending.Context.RunId != runId)
            {
                throw new InvalidOperationException(
                    $"Pending interaction belongs to run '{pending.Context.RunId:N}', not '{runId:N}'."
                );
            }
        }

        if (!pending.TryBeginSubmission())
        {
            throw new InvalidOperationException(
                $"Interaction '{pending.Context.InteractionId}' is already accepting an answer."
            );
        }

        var answer = new HumanAnswer(answerText.Trim());
        try
        {
            await records.AcceptHumanAnswerAsync(
                pending.Context.RequestId,
                pending.Context.InteractionId,
                pending.Context.Request,
                answer,
                CancellationToken.None
            );
        }
        catch
        {
            pending.ReleaseSubmission();
            throw;
        }

        lock (_sync)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }
        if (!pending.Completion.TrySetResult(answer))
        {
            throw new InvalidOperationException(
                $"Interaction '{pending.Context.InteractionId}' no longer accepts answers."
            );
        }
    }

    private sealed class Pending(PipelineInteractionContext<HumanQuestion, HumanAnswer> context)
    {
        private int _submitting;
        public PipelineInteractionContext<HumanQuestion, HumanAnswer> Context { get; } = context;
        public TaskCompletionSource<HumanAnswer> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool TryBeginSubmission() => Interlocked.CompareExchange(ref _submitting, 1, 0) == 0;

        public void ReleaseSubmission() => Volatile.Write(ref _submitting, 0);
    }
}
