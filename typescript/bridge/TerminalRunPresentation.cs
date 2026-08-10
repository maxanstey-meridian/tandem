using Tandem.Ledger;
using Tandem.Terminal;

namespace Tandem.NodeApiSpike;

internal sealed class TerminalRunPresentation : IAsyncDisposable
{
    private readonly TerminalPipelineDisplay _display;
    private readonly PresentationObserver _presentationObserver;
    private Exception? _failure;

    public TerminalRunPresentation(
        PipelineInspection pipeline,
        Guid runId,
        CancellationTokenSource runCancellation,
        IReadOnlyDictionary<string, string> modelNames
    )
    {
        _display = new TerminalPipelineDisplay(
            pipeline,
            runId,
            new TerminalDisplayOptions
            {
                CancelAsync = _ =>
                {
                    runCancellation.Cancel();
                    return ValueTask.CompletedTask;
                },
                ModelNames = modelNames,
            }
        );
        _presentationObserver = new PresentationObserver(this);
    }

    public IPipelineObserver ComposeObserver(IPipelinePersistenceObserver? persistenceObserver) =>
        persistenceObserver is null
            ? _presentationObserver
            : new PersistenceFirstObserver(persistenceObserver, _presentationObserver);

    public Task StartAsync(CancellationToken cancellationToken) =>
        _display.StartAsync(cancellationToken);

    public async ValueTask CompleteAsync(
        LedgerRunStatus status,
        string? summary,
        bool preserveActiveFailure
    )
    {
        try
        {
            await SignalAsync(status, summary);
            await _display.WaitForCleanupAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _failure ??= exception;
        }

        if (_failure is not null && !preserveActiveFailure)
        {
            throw new InvalidOperationException("Terminal presentation failed.", _failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _display.DisposeAsync();
        }
        catch (Exception exception)
        {
            _failure ??= exception;
        }
    }

    private ValueTask SignalAsync(LedgerRunStatus status, string? summary) =>
        status switch
        {
            LedgerRunStatus.Ready => _display.SucceededAsync(summary ?? "Pipeline completed"),
            LedgerRunStatus.Failed => _display.FailedAsync(summary ?? "Pipeline declared failure"),
            LedgerRunStatus.Cancelled => _display.CancelledAsync(summary ?? "Pipeline cancelled"),
            _ => _display.FaultedAsync(summary ?? "Pipeline faulted"),
        };

    private sealed class PresentationObserver(TerminalRunPresentation owner) : IPipelineObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            try
            {
                await owner._display.Observer.ObserveAsync(observation, cancellationToken);
            }
            catch (Exception exception)
            {
                owner._failure ??= exception;
            }
        }
    }

    private sealed class PersistenceFirstObserver(
        IPipelinePersistenceObserver persistenceObserver,
        IPipelineObserver presentationObserver
    ) : IPipelinePersistenceObserver
    {
        public async ValueTask ObserveAsync(
            PipelineObservation observation,
            CancellationToken cancellationToken
        )
        {
            await persistenceObserver.ObserveAsync(observation, cancellationToken);
            await presentationObserver.ObserveAsync(observation, cancellationToken);
        }
    }
}
