using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Tandem.Ledger;

public sealed class SqliteLedgerStore
{
    private const int SchemaVersion = 2;
    private const int MaximumLedgerToolValueCharacters = 4_000;
    private const int MaximumLedgerToolPageCharacters = 200_000;
    private const string JournalStream = "runtime.journal";
    private const string SelectJournal =
        "SELECT id AS Id, sequence AS Sequence, record AS Record, recorded_at AS RecordedAt FROM journal";
    private const string SelectRun =
        "SELECT composition AS Composition, status AS Status, started_at AS StartedAt, updated_at AS UpdatedAt, ended_at AS EndedAt FROM runs WHERE run_id = @RunId;";
    private const string Schema = """
        CREATE TABLE runs (
            run_id TEXT PRIMARY KEY,
            composition TEXT NOT NULL CHECK (length(trim(composition)) > 0),
            status TEXT NOT NULL CHECK (status IN ('Running', 'Ready', 'Failed', 'Faulted', 'Interrupted', 'Cancelled')),
            started_at INTEGER NOT NULL,
            updated_at INTEGER NOT NULL,
            ended_at INTEGER NULL
        );
        CREATE TABLE journal (
            id INTEGER PRIMARY KEY,
            run_id TEXT NOT NULL REFERENCES runs(run_id),
            sequence INTEGER NOT NULL CHECK (sequence >= 1),
            record TEXT NOT NULL CHECK (json_valid(record)),
            recorded_at INTEGER NOT NULL,
            UNIQUE (run_id, sequence)
        );
        PRAGMA user_version = 2;
        """;

    private static readonly JsonSerializerOptions _serializerOptions =
        TandemJson.CreateTypedContract();

    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly string _readOnlyConnectionString;
    private readonly Lock _initializationGate = new();
    private Task? _initialization;

    public SqliteLedgerStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        // Each operation opens its own short-lived connection. A store has no lifetime of its
        // own, so pooled connections would keep the database files open after it is dropped.
        // Default Timeout is how long Microsoft.Data.Sqlite waits on a locked database.
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        };
        _connectionString = builder.ToString();
        builder.Mode = SqliteOpenMode.ReadOnly;
        _readOnlyConnectionString = builder.ToString();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        await EnsureInitializedAsync().WaitAsync(cancellationToken);

    private Task EnsureInitializedAsync()
    {
        lock (_initializationGate)
        {
            if (_initialization is null || _initialization.IsFaulted || _initialization.IsCanceled)
            {
                _initialization = InitializeCoreAsync();
            }
            return _initialization;
        }
    }

    private async Task InitializeCoreAsync()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await connection.ExecuteAsync("PRAGMA journal_mode = WAL;");
        // The version read shares the write lock with schema creation, so concurrent
        // first-time initialisers cannot both see an empty database.
        await using var transaction = connection.BeginTransaction(deferred: false);
        var version = await connection.ExecuteScalarAsync<long>(
            "PRAGMA user_version;",
            transaction: transaction
        );
        if (version == SchemaVersion)
        {
            return;
        }
        EnsureSchemaVersion(version == 0 ? SchemaVersion : version);
        await connection.ExecuteAsync(Schema, transaction: transaction);
        await transaction.CommitAsync();
    }

    private static void EnsureSchemaVersion(long version)
    {
        if (version != SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Ledger schema version '{version}' is not supported; expected '{SchemaVersion}'."
            );
        }
    }

    public RunLedger ForRun(Guid runId) => new(this, runId);

    public async ValueTask<SqlitePipelineObserver> CreateObserverAsync(
        Guid runId,
        string composition,
        CancellationToken cancellationToken = default
    )
    {
        var run = await CreateRunAsync(runId, composition, cancellationToken);
        if (run.Status != LedgerRunStatus.Running)
        {
            throw new LedgerConflictException(
                $"Run '{runId:N}' is already terminal with status '{run.Status}'."
            );
        }
        return new SqlitePipelineObserver(this, runId);
    }

    public ValueTask<SqlitePipelineObserver> CreateObserverAsync<TState>(
        Guid runId,
        Pipeline<TState> pipeline,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        return CreateObserverAsync(runId, pipeline.Inspect().Name, cancellationToken);
    }

    internal ValueTask<LedgerRun> CreateRunAsync(
        Guid runId,
        string composition,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composition);
        return WithWriteTransactionAsync(
            async (connection, transaction, ct) =>
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT INTO runs (run_id, composition, status, started_at, updated_at)
                        VALUES (@RunId, @Composition, 'Running', @Now, @Now)
                        ON CONFLICT (run_id) DO NOTHING;
                        """,
                        new
                        {
                            RunId = Key(runId),
                            Composition = composition,
                            Now = NowMilliseconds(),
                        },
                        transaction,
                        cancellationToken: ct
                    )
                );
                var run = await ReadRunAsync(connection, transaction, runId, ct);
                return string.Equals(run.Composition, composition, StringComparison.Ordinal)
                    ? run
                    : throw new LedgerConflictException(
                        $"Run '{runId:N}' already belongs to composition '{run.Composition}'."
                    );
            },
            cancellationToken
        );
    }

    public async ValueTask<LedgerRun> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        return await ReadRunAsync(connection, null, runId, cancellationToken);
    }

    public ValueTask<LedgerRun> CompleteRunAsync(
        Guid runId,
        LedgerRunStatus status,
        CancellationToken cancellationToken = default
    )
    {
        if (status == LedgerRunStatus.Running)
        {
            throw new ArgumentException("A terminal run status is required.", nameof(status));
        }
        return WithWriteTransactionAsync(
            async (connection, transaction, ct) =>
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE runs SET status = @Status, updated_at = @Now, ended_at = @Now
                        WHERE run_id = @RunId AND status = 'Running';
                        """,
                        new
                        {
                            RunId = Key(runId),
                            Status = status.ToString(),
                            Now = NowMilliseconds(),
                        },
                        transaction,
                        cancellationToken: ct
                    )
                );
                var run = await ReadRunAsync(connection, transaction, runId, ct);
                return run.Status == status
                    ? run
                    : throw new LedgerConflictException(
                        $"Run '{runId:N}' is already terminal with status '{run.Status}'."
                    );
            },
            cancellationToken
        );
    }

    internal ValueTask<long> AppendAsync(
        Guid runId,
        RuntimeJournalRecord record,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.Serialize(record, _serializerOptions);
        return WithWriteTransactionAsync(
            async (connection, transaction, ct) =>
            {
                var sequence = await connection.QuerySingleOrDefaultAsync<long?>(
                    new CommandDefinition(
                        """
                        INSERT INTO journal (run_id, sequence, record, recorded_at)
                        SELECT @RunId,
                            COALESCE((SELECT MAX(sequence) FROM journal WHERE run_id = @RunId), 0) + 1,
                            @Record,
                            @Now
                        WHERE EXISTS (SELECT 1 FROM runs WHERE run_id = @RunId AND status = 'Running')
                        RETURNING sequence;
                        """,
                        new
                        {
                            RunId = Key(runId),
                            Record = payload,
                            Now = NowMilliseconds(),
                        },
                        transaction,
                        cancellationToken: ct
                    )
                );
                if (sequence is { } appended)
                {
                    return appended;
                }
                var run = await ReadRunAsync(connection, transaction, runId, ct);
                throw new LedgerConflictException(
                    $"Run '{runId:N}' is already terminal with status '{run.Status}'."
                );
            },
            cancellationToken
        );
    }

    public async ValueTask<IReadOnlyList<LedgerJournalEntry>> ReadJournalAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    ) => (await ReadJournalRowsAsync(runId, cancellationToken)).Select(ToEntry).ToList();

    public async ValueTask<IReadOnlyList<RuntimeJournalRecord>> ReadAcceptedAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    ) =>
        (await ReadJournalRowsAsync(runId, cancellationToken))
            .Select(Deserialize)
            .Where(PipelineJournal.IsAccepted)
            .ToList();

    public ValueTask<AcceptedPipelineValue<TValue>?> ReadLatestAcceptedAsync<TValue>(
        Guid runId,
        string stepId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        return ReadLatestAcceptedCoreAsync<TValue>(runId, stepId, cancellationToken);
    }

    public ValueTask<AcceptedPipelineValue<TValue>?> ReadLatestAcceptedAsync<TValue>(
        Guid runId,
        CancellationToken cancellationToken = default
    ) => ReadLatestAcceptedCoreAsync<TValue>(runId, null, cancellationToken);

    private async ValueTask<AcceptedPipelineValue<TValue>?> ReadLatestAcceptedCoreAsync<TValue>(
        Guid runId,
        string? stepId,
        CancellationToken cancellationToken
    )
    {
        var expectedType = typeof(TValue).FullName ?? typeof(TValue).Name;
        var latest = (await ReadJournalAsync(runId, cancellationToken)).LastOrDefault(entry =>
            PipelineJournal.IsAccepted(entry.Record)
            && (
                stepId is null
                    ? string.Equals(entry.Record.ValueType, expectedType, StringComparison.Ordinal)
                    : string.Equals(entry.Record.StepId, stepId, StringComparison.Ordinal)
            )
        );
        if (latest is null)
        {
            return null;
        }

        var location = stepId is null
            ? $"Accepted value at sequence '{latest.Sequence}'"
            : $"Accepted value at sequence '{latest.Sequence}' for step '{stepId}'";
        if (string.IsNullOrWhiteSpace(latest.Record.ValueType))
        {
            throw new LedgerDataException($"{location} has no value type.");
        }
        if (!string.Equals(latest.Record.ValueType, expectedType, StringComparison.Ordinal))
        {
            throw new LedgerValueTypeMismatchException(
                $"{location} is '{latest.Record.ValueType}', not '{expectedType}'."
            );
        }
        var payload =
            latest.Record.Payload ?? throw new LedgerDataException($"{location} has no payload.");
        TValue value;
        try
        {
            value =
                payload.Deserialize<TValue>(_serializerOptions)
                ?? throw new LedgerDataException($"{location} is null.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new LedgerDataException($"{location} is malformed.", exception);
        }
        return new AcceptedPipelineValue<TValue>(
            latest.Sequence,
            latest.Record.StepId,
            expectedType,
            value,
            latest.RecordedAt
        );
    }

    internal async ValueTask<PipelineLedgerPage> ReadPageAsync(
        Guid runId,
        string? query,
        long? cursor,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (cursor < 0)
        {
            throw new Tandem.Infrastructure.PaginationValidationException(
                nameof(cursor),
                "Cursor cannot be negative. Restart with a null cursor; subsequently use nextCursor from the preceding page.",
                new { cursor, retryCursor = (long?)null }
            );
        }
        if (limit is < 1 or > 50)
        {
            throw new Tandem.Infrastructure.PaginationValidationException(
                nameof(limit),
                "Ledger page size must be 1 to 50.",
                new
                {
                    limit,
                    minimumLimit = 1,
                    maximumLimit = 50,
                    retryLimit = Math.Clamp(limit, 1, 50),
                }
            );
        }
        if (query is not null)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                throw new Tandem.Infrastructure.PaginationValidationException(
                    nameof(query),
                    "Supply nonblank search text, or use read_ledger to browse without a query.",
                    new { minimumQueryLength = 1, maximumQueryLength = 1024 }
                );
            }
            if (query.Length > 1_024)
            {
                throw new Tandem.Infrastructure.PaginationValidationException(
                    nameof(query),
                    "Ledger search query cannot exceed 1024 characters. Shorten the query and restart with a null cursor.",
                    new
                    {
                        queryLength = query.Length,
                        maximumQueryLength = 1024,
                        retryCursor = (long?)null,
                    }
                );
            }
        }
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        var rows = await connection.QueryAsync<JournalRow>(
            new CommandDefinition(
                $"{SelectJournal} WHERE run_id = @RunId AND id > @Cursor ORDER BY id;",
                new { RunId = Key(runId), Cursor = cursor ?? 0 },
                cancellationToken: cancellationToken
            )
        );
        var entries = new List<PipelineLedgerEntry>();
        var pageCharacters = 0;
        var hasMore = false;
        foreach (var row in rows)
        {
            var value = row.Record;
            if (!IsAgentReadableLedgerEntry(value) || !Matches(value, query))
            {
                continue;
            }
            var formatted = FormatLedgerToolValue(value, query);
            if (
                entries.Count >= limit
                || pageCharacters + formatted.Length > MaximumLedgerToolPageCharacters
            )
            {
                hasMore = true;
                break;
            }
            entries.Add(
                new PipelineLedgerEntry(
                    row.Id,
                    JournalStream,
                    row.Sequence,
                    $"{JournalStream}-{row.Sequence}",
                    formatted,
                    FromUnix(row.RecordedAt)
                )
            );
            pageCharacters += formatted.Length;
        }
        return new PipelineLedgerPage(entries, hasMore ? entries[^1].Cursor : null);
    }

    internal async ValueTask<long?> FindActionEntryAsync(
        Guid runId,
        string stepId,
        string invocationId,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        var rows = await connection.QueryAsync<JournalRow>(
            new CommandDefinition(
                $"{SelectJournal} WHERE run_id = @RunId ORDER BY id DESC;",
                new { RunId = Key(runId) },
                cancellationToken: cancellationToken
            )
        );
        return rows.FirstOrDefault(row =>
            Deserialize(row)
                is { Kind: RuntimeJournalKind.ActionCompleted, Payload: not null } record
            && record.StepId == stepId
            && record.Identity == invocationId
        )?.Id;
    }

    internal async ValueTask<object> ReadEntryPageAsync(
        Guid runId,
        long entryCursor,
        int offset,
        int limit,
        CancellationToken cancellationToken,
        string? diagnosticStream = null
    )
    {
        if (entryCursor <= 0 || offset < 0 || limit is < 2 or > 65536)
        {
            throw new Tandem.Infrastructure.PaginationValidationException(
                "page",
                "entryCursor must be positive, offset nonnegative, and limit from 2 to 65536.",
                new
                {
                    entryCursor,
                    retryOffset = 0,
                    retryLimit = Math.Clamp(limit, 2, 65536),
                }
            );
        }

        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        var value =
            await connection.QuerySingleOrDefaultAsync<string>(
                new CommandDefinition(
                    "SELECT record FROM journal WHERE run_id = @RunId AND id = @Cursor;",
                    new { RunId = Key(runId), Cursor = entryCursor },
                    cancellationToken: cancellationToken
                )
            )
            ?? throw new Tandem.Infrastructure.PaginationValidationException(
                nameof(entryCursor),
                "No readable entry at this cursor in the current run. Use read_ledger or search_ledger to obtain an entry cursor.",
                new { entryCursor }
            );
        if (!IsAgentReadableLedgerEntry(value))
        {
            throw new Tandem.Infrastructure.PaginationValidationException(
                nameof(entryCursor),
                "This record is not agent-readable. Use a cursor from read_ledger or search_ledger.",
                new { entryCursor }
            );
        }

        bool? captureTruncated = null;
        if (diagnosticStream is not null)
        {
            if (diagnosticStream is not ("stdout" or "stderr"))
            {
                throw new Tandem.Infrastructure.ToolInputException(
                    "stream must be stdout or stderr."
                );
            }

            var record = JsonSerializer.Deserialize<RuntimeJournalRecord>(
                value,
                _serializerOptions
            );
            if (
                record
                is not { Kind: RuntimeJournalKind.ActionCompleted, Payload: { } processPayload }
            )
            {
                throw new Tandem.Infrastructure.ToolInputException(
                    "This entry has no process diagnostics. Read it without stream."
                );
            }

            var process =
                processPayload.Deserialize<PipelineActionProcessPayload>()
                ?? throw new LedgerDataException("Missing process output.");
            value = diagnosticStream == "stdout" ? process.Stdout : process.Stderr;
            captureTruncated = process.Truncated;
        }

        if (
            offset > value.Length
            || (
                offset > 0
                && offset < value.Length
                && char.IsLowSurrogate(value[offset])
                && char.IsHighSurrogate(value[offset - 1])
            )
        )
        {
            throw new Tandem.Infrastructure.PaginationValidationException(
                nameof(offset),
                "Offset exceeds the entry or splits a Unicode character. Restart at offset 0 and follow nextOffset.",
                new { totalLength = value.Length, retryOffset = 0 }
            );
        }

        var length = Math.Min(limit, value.Length - offset);
        if (
            length > 0
            && offset + length < value.Length
            && char.IsHighSurrogate(value[offset + length - 1])
        )
        {
            length--;
        }

        var next = offset + length;
        return new
        {
            entryCursor,
            stream = diagnosticStream,
            captureTruncated,
            content = value.Substring(offset, length),
            offset,
            length,
            totalLength = value.Length,
            hasMore = next < value.Length,
            nextOffset = next < value.Length ? (int?)next : null,
            offsetUnit = "UTF-16 code units",
        };
    }

    private static bool IsAgentReadableLedgerEntry(string value)
    {
        try
        {
            var record = JsonSerializer.Deserialize<RuntimeJournalRecord>(
                value,
                _serializerOptions
            );
            return record is not null
                && (PipelineJournal.IsAccepted(record) || IsReadableCommand(record));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new LedgerDataException(
                "The runtime journal contains a malformed record.",
                exception
            );
        }
    }

    private static bool IsReadableCommand(RuntimeJournalRecord record)
    {
        if (record is { Kind: RuntimeJournalKind.CommandCompleted, Payload: not null })
        {
            return true;
        }
        if (record is not { Kind: RuntimeJournalKind.ActionCompleted, Payload: { } payload })
        {
            return false;
        }
        try
        {
            return payload.Deserialize<PipelineActionProcessPayload>() is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Matches(string value, string? query) =>
        query is null || value.Contains(query, StringComparison.OrdinalIgnoreCase);

    private static string FormatLedgerToolValue(string value, string? query)
    {
        if (value.Length <= MaximumLedgerToolValueCharacters)
        {
            return value;
        }

        var start = 0;
        if (query is not null)
        {
            var match = value.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            start = Math.Max(0, match - MaximumLedgerToolValueCharacters / 4);
            start = Math.Min(start, value.Length - MaximumLedgerToolValueCharacters);
        }
        var prefix = start > 0 ? "[...truncated...]" : "";
        var suffix =
            start + MaximumLedgerToolValueCharacters < value.Length ? "[...truncated...]" : "";
        return $"{prefix}{value.Substring(start, MaximumLedgerToolValueCharacters)}{suffix}";
    }

    private async ValueTask<IEnumerable<JournalRow>> ReadJournalRowsAsync(
        Guid runId,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        await ReadRunAsync(connection, null, runId, cancellationToken);
        return await connection.QueryAsync<JournalRow>(
            new CommandDefinition(
                $"{SelectJournal} WHERE run_id = @RunId ORDER BY id;",
                new { RunId = Key(runId) },
                cancellationToken: cancellationToken
            )
        );
    }

    private async ValueTask<T> WithWriteTransactionAsync<T>(
        Func<SqliteConnection, SqliteTransaction, CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync().WaitAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var result = await operation(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async ValueTask<SqliteConnection> OpenReadOnlyAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_readOnlyConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            EnsureSchemaVersion(await connection.ExecuteScalarAsync<long>("PRAGMA user_version;"));
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async ValueTask<LedgerRun> ReadRunAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid runId,
        CancellationToken cancellationToken
    )
    {
        var row =
            await connection.QuerySingleOrDefaultAsync<RunRow>(
                new CommandDefinition(
                    SelectRun,
                    new { RunId = Key(runId) },
                    transaction,
                    cancellationToken: cancellationToken
                )
            ) ?? throw new KeyNotFoundException($"Run '{runId:N}' does not exist.");
        return new LedgerRun(
            runId,
            row.Composition,
            Enum.Parse<LedgerRunStatus>(row.Status),
            FromUnix(row.StartedAt),
            FromUnix(row.UpdatedAt),
            row.EndedAt is { } endedAt ? FromUnix(endedAt) : null
        );
    }

    private static LedgerJournalEntry ToEntry(JournalRow row) =>
        new(row.Sequence, Deserialize(row), FromUnix(row.RecordedAt));

    private static RuntimeJournalRecord Deserialize(JournalRow row)
    {
        try
        {
            return JsonSerializer.Deserialize<RuntimeJournalRecord>(row.Record, _serializerOptions)
                ?? throw new JsonException("Journal record is null.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new LedgerDataException($"Journal record '{row.Id}' is malformed.", exception);
        }
    }

    private static string Key(Guid runId) => runId.ToString("N");

    private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static DateTimeOffset FromUnix(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private sealed record JournalRow(long Id, long Sequence, string Record, long RecordedAt);

    private sealed record RunRow(
        string Composition,
        string Status,
        long StartedAt,
        long UpdatedAt,
        long? EndedAt
    );
}
