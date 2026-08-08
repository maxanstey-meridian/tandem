using Tandem.Advanced;
using Tandem.Ledger;

namespace Tandem.Tool;

internal sealed class LedgerUnitOfWork(SqliteLedgerStore store) : IPipelineAcceptanceUnitOfWork
{
    public ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken
    ) => store.ExecuteAsync(operation, cancellationToken);
}
