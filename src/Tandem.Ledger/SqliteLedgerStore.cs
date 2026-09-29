using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Tandem.Ledger;

public sealed class SqliteLedgerStore
{
    private const int SchemaVersion = 2;
    private const int MaximumLedgerToolValueCharacters = 4_000;
    private const int MaximumLedgerToolPageCharacters = 200_000;
    private const string JournalStream = "runtime.journal";
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly SqliteLedgerOptions _options;

    public SqliteLedgerStore(
        string databasePath,
        TimeProvider? timeProvider = null,
        JsonSerializerOptions? serializerOptions = null,
        SqliteLedgerOptions? options = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // WAL permits readers alongside a writer. Shared cache adds table
            // locks that defeat that isolation between concurrent run stores.
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serializerOptions = serializerOptions ?? TandemJson.CreateTypedContract();
        _options = options ?? SqliteLedgerOptions.Default;
        if (_options.BusyTimeout <= TimeSpan.Zero || _options.BusyTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Busy timeout must be between zero and one minute."
            );
        }
        if (_options.LockRetryAttempts < 0 || _options.LockRetryAttempts > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Lock retry attempts must be between zero and ten."
            );
        }
        if (
            _options.LockRetryDelay < TimeSpan.Zero
            || _options.LockRetryDelay > TimeSpan.FromSeconds(10)
        )
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Lock retry delay must be between zero and ten seconds."
            );
        }
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default) =>
        await RetryLockedAsync(
            async ct =>
            {
                await InitializeCoreAsync(ct);
                return true;
            },
            cancellationToken
        );

    private async ValueTask InitializeCoreAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken);

        // The version read shares the write lock with schema creation, so concurrent
        // first-time initialisers cannot both see an empty database.
        await using var transaction = connection.BeginTransaction(deferred: false);
        var version = await ScalarAsync<long>(
            connection,
            transaction,
            "PRAGMA user_version;",
            cancellationToken
        );
        if (version is not 0 and not SchemaVersion)
        {
            throw new InvalidOperationException(
                $"Ledger schema version '{version}' is not supported; expected '{SchemaVersion}'."
            );
        }

        if (version == SchemaVersion)
        {
            return;
        }

        await ExecuteAsync(
            connection,
            """
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
            """,
            cancellationToken,
            transaction
        );
        await transaction.CommitAsync(cancellationToken);
    }

    internal async ValueTask<LedgerRun> CreateRunAsync(
        Guid runId,
        string composition,
        CancellationToken cancellationToken = default
    ) =>
        await RetryLockedAsync(ct => CreateRunCoreAsync(runId, composition, ct), cancellationToken);

    private async ValueTask<LedgerRun> CreateRunCoreAsync(
        Guid runId,
        string composition,
        CancellationToken cancellationToken
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(composition);
        var now = Now();
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs (run_id, composition, status, started_at, updated_at)
            VALUES ($run_id, $composition, 'Running', $now, $now)
            ON CONFLICT (run_id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$composition", composition);
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);

        var run = await ReadRunAsync(connection, runId, cancellationToken);
        if (!string.Equals(run.Composition, composition, StringComparison.Ordinal))
        {
            throw new LedgerConflictException(
                $"Run '{runId:N}' already belongs to composition '{run.Composition}'."
            );
        }
        return run;
    }

    public RunLedger ForRun(Guid runId) => new(this, runId);

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
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, sequence, record, recorded_at
            FROM journal
            WHERE run_id = $run_id AND id > $cursor
            ORDER BY id;
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$cursor", cursor ?? 0);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<PipelineLedgerEntry>();
        var pageCharacters = 0;
        var hasMore = false;
        while (await reader.ReadAsync(cancellationToken))
        {
            var value = reader.GetString(2);
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
                    reader.GetInt64(0),
                    JournalStream,
                    reader.GetInt64(1),
                    $"{JournalStream}-{reader.GetInt64(1)}",
                    formatted,
                    FromUnix(reader.GetInt64(3))
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
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, record FROM journal WHERE run_id = $run ORDER BY id DESC;";
        command.Parameters.AddWithValue("$run", runId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var record = JsonSerializer.Deserialize<RuntimeJournalRecord>(
                reader.GetString(1),
                _serializerOptions
            );
            if (
                record is { Kind: RuntimeJournalKind.ActionCompleted, Payload: not null }
                && record.StepId == stepId
                && record.Identity == invocationId
            )
            {
                return reader.GetInt64(0);
            }
        }
        return null;
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

        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT record FROM journal WHERE run_id = $run_id AND id = $cursor;";
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$cursor", entryCursor);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new Tandem.Infrastructure.PaginationValidationException(
                nameof(entryCursor),
                "No readable entry at this cursor in the current run. Use read_ledger or search_ledger to obtain an entry cursor.",
                new { entryCursor }
            );
        }

        var value = reader.GetString(0);
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

    private bool IsAgentReadableLedgerEntry(string value)
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

    public async ValueTask<SqlitePipelineObserver> CreateObserverAsync(
        Guid runId,
        string composition,
        CancellationToken cancellationToken = default
    )
    {
        await InitializeAsync(cancellationToken);
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

    public async ValueTask<IReadOnlyList<RuntimeJournalRecord>> ReadAcceptedAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = _databasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()
        );
        await connection.OpenAsync(cancellationToken);
        await ReadRunAsync(connection, runId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT record FROM journal WHERE run_id = $run_id ORDER BY id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var accepted = new List<RuntimeJournalRecord>();
        while (await reader.ReadAsync(cancellationToken))
        {
            RuntimeJournalRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<RuntimeJournalRecord>(
                    reader.GetString(0),
                    _serializerOptions
                );
            }
            catch (JsonException exception)
            {
                throw new LedgerDataException(
                    $"Run '{runId:N}' contains a malformed pipeline journal record.",
                    exception
                );
            }
            if (record is not null && PipelineJournal.IsAccepted(record))
            {
                accepted.Add(record);
            }
        }
        return accepted;
    }

    public async ValueTask<IReadOnlyList<LedgerJournalEntry>> ReadJournalAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    )
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await ReadRunAsync(connection, runId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sequence, record, recorded_at FROM journal WHERE run_id = $run_id ORDER BY id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<LedgerJournalEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            RuntimeJournalRecord record;
            try
            {
                record =
                    JsonSerializer.Deserialize<RuntimeJournalRecord>(
                        reader.GetString(1),
                        _serializerOptions
                    ) ?? throw new JsonException("Journal record was null.");
            }
            catch (JsonException exception)
            {
                throw new LedgerDataException(
                    $"Run '{runId:N}' contains a malformed pipeline journal record.",
                    exception
                );
            }
            entries.Add(
                new LedgerJournalEntry(reader.GetInt64(0), record, FromUnix(reader.GetInt64(2)))
            );
        }
        return entries;
    }

    public async ValueTask<AcceptedPipelineValue<TValue>?> ReadLatestAcceptedAsync<TValue>(
        Guid runId,
        string stepId,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        var entries = await ReadJournalAsync(runId, cancellationToken);
        var latest = entries.LastOrDefault(entry =>
            string.Equals(entry.Record.StepId, stepId, StringComparison.Ordinal)
            && PipelineJournal.IsAccepted(entry.Record)
        );
        if (latest is null)
        {
            return null;
        }

        var expectedType = typeof(TValue).FullName ?? typeof(TValue).Name;
        if (string.IsNullOrWhiteSpace(latest.Record.ValueType))
        {
            throw new LedgerDataException(
                $"Accepted value at sequence '{latest.Sequence}' for step '{stepId}' has no value type."
            );
        }
        if (!string.Equals(latest.Record.ValueType, expectedType, StringComparison.Ordinal))
        {
            throw new LedgerValueTypeMismatchException(
                $"Accepted value at sequence '{latest.Sequence}' for step '{stepId}' is '{latest.Record.ValueType}', not '{expectedType}'."
            );
        }

        TValue value;
        try
        {
            var payload =
                latest.Record.Payload
                ?? throw new LedgerDataException(
                    $"Accepted value at sequence '{latest.Sequence}' for step '{stepId}' has no payload."
                );
            value = payload.Deserialize<TValue>(_serializerOptions)!;
            if (value is null)
            {
                throw new LedgerDataException(
                    $"Accepted value at sequence '{latest.Sequence}' for step '{stepId}' is null."
                );
            }
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new LedgerDataException(
                $"Accepted value at sequence '{latest.Sequence}' for step '{stepId}' is malformed.",
                exception
            );
        }
        return new AcceptedPipelineValue<TValue>(
            latest.Sequence,
            latest.Record.StepId,
            latest.Record.ValueType,
            value,
            latest.RecordedAt
        );
    }

    public async ValueTask<AcceptedPipelineValue<TValue>?> ReadLatestAcceptedAsync<TValue>(
        Guid runId,
        CancellationToken cancellationToken = default
    )
    {
        var expectedType = typeof(TValue).FullName ?? typeof(TValue).Name;
        var entries = await ReadJournalAsync(runId, cancellationToken);
        var latest = entries.LastOrDefault(entry =>
            PipelineJournal.IsAccepted(entry.Record)
            && string.Equals(entry.Record.ValueType, expectedType, StringComparison.Ordinal)
        );
        if (latest is null)
        {
            return null;
        }
        var payload =
            latest.Record.Payload
            ?? throw new LedgerDataException(
                $"Accepted value at sequence '{latest.Sequence}' has no payload."
            );
        TValue value;
        try
        {
            value =
                payload.Deserialize<TValue>(_serializerOptions)
                ?? throw new LedgerDataException(
                    $"Accepted value at sequence '{latest.Sequence}' is null."
                );
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new LedgerDataException(
                $"Accepted value at sequence '{latest.Sequence}' is malformed.",
                exception
            );
        }
        return new AcceptedPipelineValue<TValue>(
            latest.Sequence,
            latest.Record.StepId,
            expectedType,
            value,
            latest.RecordedAt
        );
    }

    public async ValueTask<LedgerRun> GetRunAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    )
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadRunAsync(connection, runId, cancellationToken);
    }

    public async ValueTask<LedgerRun> CompleteRunAsync(
        Guid runId,
        LedgerRunStatus status,
        CancellationToken cancellationToken = default
    ) => await RetryLockedAsync(ct => CompleteRunCoreAsync(runId, status, ct), cancellationToken);

    private async ValueTask<LedgerRun> CompleteRunCoreAsync(
        Guid runId,
        LedgerRunStatus status,
        CancellationToken cancellationToken
    )
    {
        if (status == LedgerRunStatus.Running)
        {
            throw new ArgumentException("A terminal run status is required.", nameof(status));
        }
        var now = Now();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE runs
            SET status = $status, updated_at = $now, ended_at = $now
            WHERE run_id = $run_id AND status = 'Running';
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);

        var run = await ReadRunAsync(connection, runId, cancellationToken, transaction);
        if (run.Status != status)
        {
            throw new LedgerConflictException(
                $"Run '{runId:N}' is already terminal with status '{run.Status}'."
            );
        }
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    internal async ValueTask<long> AppendAsync(
        Guid runId,
        RuntimeJournalRecord record,
        CancellationToken cancellationToken
    ) => await RetryLockedAsync(ct => AppendCoreAsync(runId, record, ct), cancellationToken);

    private async ValueTask<long> AppendCoreAsync(
        Guid runId,
        RuntimeJournalRecord record,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.Serialize(record, _serializerOptions);
        var now = Now();
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        await EnsureRunRunningAsync(connection, transaction, runId, cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO journal (run_id, sequence, record, recorded_at)
            VALUES (
                $run_id,
                COALESCE((SELECT MAX(sequence) + 1 FROM journal WHERE run_id = $run_id), 1),
                $record,
                $recorded_at
            )
            RETURNING sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$record", payload);
        command.Parameters.AddWithValue("$recorded_at", now.ToUnixTimeMilliseconds());
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return sequence;
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString)
        {
            DefaultTimeout = Math.Max(1, (int)Math.Ceiling(_options.BusyTimeout.TotalSeconds)),
        };
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecuteAsync(
            connection,
            $"PRAGMA busy_timeout = {(long)_options.BusyTimeout.TotalMilliseconds};",
            cancellationToken
        );
        return connection;
    }

    private async ValueTask<T> RetryLockedAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await operation(cancellationToken);
            }
            catch (SqliteException exception)
                when (IsLocked(exception) && attempt < _options.LockRetryAttempts)
            {
                await Task.Delay(_options.LockRetryDelay, cancellationToken);
            }
        }
    }

    private static bool IsLocked(SqliteException exception) => exception.SqliteErrorCode is 5 or 6;

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<T> ScalarAsync<T>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? throw new InvalidOperationException("SQLite returned no scalar value.")
            : (T)Convert.ChangeType(value, typeof(T));
    }

    private async ValueTask<LedgerRun> ReadRunAsync(
        SqliteConnection connection,
        Guid runId,
        CancellationToken cancellationToken,
        SqliteTransaction? transaction = null
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT composition, status, started_at, updated_at, ended_at FROM runs WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException($"Run '{runId:N}' does not exist.");
        }
        return new LedgerRun(
            runId,
            reader.GetString(0),
            Enum.Parse<LedgerRunStatus>(reader.GetString(1)),
            FromUnix(reader.GetInt64(2)),
            FromUnix(reader.GetInt64(3)),
            reader.IsDBNull(4) ? null : FromUnix(reader.GetInt64(4))
        );
    }

    private static async ValueTask EnsureRunRunningAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT status FROM runs WHERE run_id = $run_id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            throw new KeyNotFoundException($"Run '{runId:N}' does not exist.");
        }
        var status = Enum.Parse<LedgerRunStatus>((string)result);
        if (status != LedgerRunStatus.Running)
        {
            throw new LedgerConflictException(
                $"Run '{runId:N}' is already terminal with status '{status}'."
            );
        }
    }

    private DateTimeOffset Now() => FromUnix(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

    private static DateTimeOffset FromUnix(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);
}
