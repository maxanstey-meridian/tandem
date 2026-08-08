using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Tandem.Ledger;

public sealed class SqliteLedgerStore
{
    private const int SchemaVersion = 1;
    private readonly string _connectionString;
    private readonly TimeProvider _timeProvider;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly SqliteLedgerOptions _options;
    private readonly AsyncLocal<TransactionScope?> _transaction = new();

    public SqliteLedgerStore(
        string databasePath,
        TimeProvider? timeProvider = null,
        JsonSerializerOptions? serializerOptions = null,
        SqliteLedgerOptions? options = null
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _serializerOptions =
            serializerOptions ?? new JsonSerializerOptions(JsonSerializerDefaults.Web);
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
        await using var connection = await OpenAsync(cancellationToken);
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecuteAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken);

        var version = await ScalarAsync<long>(
            connection,
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

        await using var transaction = connection.BeginTransaction(deferred: false);
        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS runs (
                run_id TEXT PRIMARY KEY,
                composition TEXT NOT NULL CHECK (length(trim(composition)) > 0),
                status TEXT NOT NULL CHECK (status IN ('Running', 'Ready', 'Failed', 'Faulted', 'Cancelled')),
                started_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                ended_at INTEGER NULL
            );
            CREATE TABLE IF NOT EXISTS ledger_contracts (
                storage_name TEXT PRIMARY KEY CHECK (length(trim(storage_name)) > 0),
                storage_kind TEXT NOT NULL CHECK (storage_kind IN ('stream', 'document')),
                contract_name TEXT NOT NULL CHECK (length(trim(contract_name)) > 0),
                contract_version INTEGER NOT NULL CHECK (contract_version >= 1)
            );
            CREATE TABLE IF NOT EXISTS run_entries (
                run_id TEXT NOT NULL,
                stream TEXT NOT NULL CHECK (length(trim(stream)) > 0),
                sequence INTEGER NOT NULL CHECK (sequence >= 1),
                entry_id TEXT NOT NULL CHECK (length(trim(entry_id)) > 0),
                payload BLOB NOT NULL,
                payload_hash BLOB NOT NULL,
                recorded_at INTEGER NOT NULL,
                PRIMARY KEY (run_id, stream, sequence),
                UNIQUE (run_id, entry_id),
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            CREATE TABLE IF NOT EXISTS run_documents (
                run_id TEXT NOT NULL,
                key TEXT NOT NULL CHECK (length(trim(key)) > 0),
                version INTEGER NOT NULL CHECK (version >= 1),
                payload BLOB NOT NULL,
                payload_hash BLOB NOT NULL,
                updated_at INTEGER NOT NULL,
                PRIMARY KEY (run_id, key),
                FOREIGN KEY (run_id) REFERENCES runs(run_id)
            );
            PRAGMA user_version = 1;
            """,
            cancellationToken,
            transaction
        );
        await transaction.CommitAsync(cancellationToken);
    }

    public async ValueTask<LedgerRun> CreateRunAsync(
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

    public async ValueTask<T> ExecuteAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (_transaction.Value is not null)
        {
            return await operation(cancellationToken);
        }

        SqliteConnection? connection = null;
        SqliteTransaction? transaction = null;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                connection = await OpenAsync(cancellationToken);
                transaction = connection.BeginTransaction(deferred: false);
                break;
            }
            catch (SqliteException exception)
                when (IsLocked(exception) && attempt < _options.LockRetryAttempts)
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync();
                }
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
                transaction = null;
                connection = null;
                await Task.Delay(_options.LockRetryDelay, cancellationToken);
            }
            catch
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync();
                }
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
                throw;
            }
        }

        var activeConnection = connection;
        var activeTransaction = transaction;
        await using (activeConnection)
        await using (activeTransaction)
        {
            _transaction.Value = new TransactionScope(activeConnection, activeTransaction);
            try
            {
                var result = await operation(cancellationToken);
                await activeTransaction.CommitAsync(CancellationToken.None);
                return result;
            }
            finally
            {
                _transaction.Value = null;
            }
        }
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
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE runs
            SET status = $status, updated_at = $now, ended_at = $now
            WHERE run_id = $run_id AND status = 'Running';
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
        await command.ExecuteNonQueryAsync(cancellationToken);

        var run = await ReadRunAsync(connection, runId, cancellationToken);
        if (run.Status != status)
        {
            throw new LedgerConflictException(
                $"Run '{runId:N}' is already terminal with status '{run.Status}'."
            );
        }
        return run;
    }

    internal async ValueTask<AcceptedLedgerEntry<TEntry>> AppendAsync<TEntry>(
        Guid runId,
        LedgerStream<TEntry> stream,
        string entryId,
        TEntry entry,
        CancellationToken cancellationToken
    ) =>
        _transaction.Value is null
            ? await RetryLockedAsync(
                ct => AppendCoreAsync(runId, stream, entryId, entry, ct),
                cancellationToken
            )
            : await AppendCoreAsync(runId, stream, entryId, entry, cancellationToken);

    private async ValueTask<AcceptedLedgerEntry<TEntry>> AppendCoreAsync<TEntry>(
        Guid runId,
        LedgerStream<TEntry> stream,
        string entryId,
        TEntry entry,
        CancellationToken cancellationToken
    )
    {
        var streamName = stream.ValidatedName;
        ArgumentException.ThrowIfNullOrWhiteSpace(entryId);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry, _serializerOptions);
        var hash = SHA256.HashData(payload);
        var now = Now();
        if (_transaction.Value is { } scope)
        {
            return await AppendInTransactionAsync(
                scope.Connection,
                scope.Transaction,
                runId,
                stream,
                streamName,
                entryId,
                entry,
                payload,
                hash,
                now,
                cancellationToken
            );
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var accepted = await AppendInTransactionAsync(
            connection,
            transaction,
            runId,
            stream,
            streamName,
            entryId,
            entry,
            payload,
            hash,
            now,
            cancellationToken
        );
        await transaction.CommitAsync(cancellationToken);
        return accepted;
    }

    private async ValueTask<AcceptedLedgerEntry<TEntry>> AppendInTransactionAsync<TEntry>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        LedgerStream<TEntry> stream,
        string streamName,
        string entryId,
        TEntry entry,
        byte[] payload,
        byte[] hash,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        await EnsureContractAsync(
            connection,
            transaction,
            streamName,
            "stream",
            stream.ValidatedContract,
            stream.ValidatedVersion,
            cancellationToken
        );

        var replay = await ReadEntryByIdAsync<TEntry>(
            connection,
            transaction,
            runId,
            entryId,
            cancellationToken
        );
        if (replay is not null)
        {
            if (
                !string.Equals(replay.Value.Stream, streamName, StringComparison.Ordinal)
                || !replay.Value.Hash.AsSpan().SequenceEqual(hash)
                || !replay.Value.Payload.AsSpan().SequenceEqual(payload)
            )
            {
                throw new LedgerConflictException(
                    $"Entry '{entryId}' already exists in run '{runId:N}' with different content."
                );
            }
            return new AcceptedLedgerEntry<TEntry>(
                replay.Value.Sequence,
                entryId,
                Deserialize<TEntry>(replay.Value.Payload),
                FromUnix(replay.Value.RecordedAt)
            );
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO run_entries
                (run_id, stream, sequence, entry_id, payload, payload_hash, recorded_at)
            VALUES (
                $run_id,
                $stream,
                COALESCE((SELECT MAX(sequence) + 1 FROM run_entries WHERE run_id = $run_id AND stream = $stream), 1),
                $entry_id,
                $payload,
                $hash,
                $recorded_at
            )
            RETURNING sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$stream", streamName);
        command.Parameters.AddWithValue("$entry_id", entryId);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$recorded_at", now.ToUnixTimeMilliseconds());
        var sequence = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return new AcceptedLedgerEntry<TEntry>(sequence, entryId, entry, now);
    }

    internal async ValueTask<IReadOnlyList<AcceptedLedgerEntry<TEntry>>> ReadAsync<TEntry>(
        Guid runId,
        LedgerStream<TEntry> stream,
        CancellationToken cancellationToken
    )
    {
        await RetryLockedAsync(
            async ct =>
            {
                await EnsureContractAsync(
                    stream.ValidatedName,
                    "stream",
                    stream.ValidatedContract,
                    stream.ValidatedVersion,
                    ct
                );
                return true;
            },
            cancellationToken
        );
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, entry_id, payload, recorded_at
            FROM run_entries
            WHERE run_id = $run_id AND stream = $stream
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$stream", stream.ValidatedName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AcceptedLedgerEntry<TEntry>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(
                new AcceptedLedgerEntry<TEntry>(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    Deserialize<TEntry>((byte[])reader[2]),
                    FromUnix(reader.GetInt64(3))
                )
            );
        }
        return entries;
    }

    internal async ValueTask<IReadOnlyList<AcceptedLedgerEntry<TEntry>>> ReadAfterAsync<TEntry>(
        Guid runId,
        LedgerStream<TEntry> stream,
        long sequence,
        CancellationToken cancellationToken
    )
    {
        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }
        await RetryLockedAsync(
            async ct =>
            {
                await EnsureContractAsync(
                    stream.ValidatedName,
                    "stream",
                    stream.ValidatedContract,
                    stream.ValidatedVersion,
                    ct
                );
                return true;
            },
            cancellationToken
        );
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, entry_id, payload, recorded_at
            FROM run_entries
            WHERE run_id = $run_id AND stream = $stream AND sequence > $sequence
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$stream", stream.ValidatedName);
        command.Parameters.AddWithValue("$sequence", sequence);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AcceptedLedgerEntry<TEntry>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(
                new AcceptedLedgerEntry<TEntry>(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    Deserialize<TEntry>((byte[])reader[2]),
                    FromUnix(reader.GetInt64(3))
                )
            );
        }
        return entries;
    }

    internal async ValueTask<IReadOnlyList<AcceptedLedgerEntry<TEntry>>> ReadRecentAsync<TEntry>(
        Guid runId,
        LedgerStream<TEntry> stream,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        await RetryLockedAsync(
            async ct =>
            {
                await EnsureContractAsync(
                    stream.ValidatedName,
                    "stream",
                    stream.ValidatedContract,
                    stream.ValidatedVersion,
                    ct
                );
                return true;
            },
            cancellationToken
        );
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT sequence, entry_id, payload, recorded_at
            FROM (
                SELECT sequence, entry_id, payload, recorded_at
                FROM run_entries
                WHERE run_id = $run_id AND stream = $stream
                ORDER BY sequence DESC
                LIMIT $limit
            )
            ORDER BY sequence;
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$stream", stream.ValidatedName);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<AcceptedLedgerEntry<TEntry>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(
                new AcceptedLedgerEntry<TEntry>(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    Deserialize<TEntry>((byte[])reader[2]),
                    FromUnix(reader.GetInt64(3))
                )
            );
        }
        return entries;
    }

    internal async ValueTask<LedgerDocumentValue<TDocument>?> ReadDocumentAsync<TDocument>(
        Guid runId,
        LedgerDocument<TDocument> document,
        CancellationToken cancellationToken
    )
    {
        if (_transaction.Value is { } scope)
        {
            await EnsureContractAsync(
                scope.Connection,
                scope.Transaction,
                document.ValidatedName,
                "document",
                document.ValidatedContract,
                document.ValidatedVersion,
                cancellationToken
            );
            return await ReadDocumentAsync<TDocument>(
                scope.Connection,
                scope.Transaction,
                runId,
                document.ValidatedName,
                cancellationToken
            );
        }

        await RetryLockedAsync(
            async ct =>
            {
                await EnsureContractAsync(
                    document.ValidatedName,
                    "document",
                    document.ValidatedContract,
                    document.ValidatedVersion,
                    ct
                );
                return true;
            },
            cancellationToken
        );
        await using var connection = await OpenAsync(cancellationToken);
        return await ReadDocumentAsync<TDocument>(
            connection,
            null,
            runId,
            document.ValidatedName,
            cancellationToken
        );
    }

    internal async ValueTask<LedgerDocumentValue<TDocument>> WriteDocumentAsync<TDocument>(
        Guid runId,
        LedgerDocument<TDocument> document,
        TDocument value,
        long expectedVersion,
        CancellationToken cancellationToken
    ) =>
        _transaction.Value is null
            ? await RetryLockedAsync(
                ct => WriteDocumentCoreAsync(runId, document, value, expectedVersion, ct),
                cancellationToken
            )
            : await WriteDocumentCoreAsync(
                runId,
                document,
                value,
                expectedVersion,
                cancellationToken
            );

    private async ValueTask<LedgerDocumentValue<TDocument>> WriteDocumentCoreAsync<TDocument>(
        Guid runId,
        LedgerDocument<TDocument> document,
        TDocument value,
        long expectedVersion,
        CancellationToken cancellationToken
    )
    {
        if (expectedVersion < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedVersion));
        }
        var key = document.ValidatedName;
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, _serializerOptions);
        var hash = SHA256.HashData(payload);
        var now = Now();
        if (_transaction.Value is { } scope)
        {
            return await WriteDocumentInTransactionAsync(
                scope.Connection,
                scope.Transaction,
                runId,
                document,
                key,
                value,
                expectedVersion,
                payload,
                hash,
                now,
                cancellationToken
            );
        }

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = connection.BeginTransaction(deferred: false);
        var written = await WriteDocumentInTransactionAsync(
            connection,
            transaction,
            runId,
            document,
            key,
            value,
            expectedVersion,
            payload,
            hash,
            now,
            cancellationToken
        );
        await transaction.CommitAsync(cancellationToken);
        return written;
    }

    private async ValueTask<
        LedgerDocumentValue<TDocument>
    > WriteDocumentInTransactionAsync<TDocument>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        LedgerDocument<TDocument> document,
        string key,
        TDocument value,
        long expectedVersion,
        byte[] payload,
        byte[] hash,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        await EnsureContractAsync(
            connection,
            transaction,
            key,
            "document",
            document.ValidatedContract,
            document.ValidatedVersion,
            cancellationToken
        );
        var current = await ReadDocumentRowAsync(
            connection,
            transaction,
            runId,
            key,
            cancellationToken
        );

        if (current is not null)
        {
            var isReplay =
                current.Value.Version == expectedVersion + 1
                && current.Value.Hash.AsSpan().SequenceEqual(hash)
                && current.Value.Payload.AsSpan().SequenceEqual(payload);
            if (isReplay)
            {
                return new LedgerDocumentValue<TDocument>(
                    current.Value.Version,
                    Deserialize<TDocument>(current.Value.Payload),
                    FromUnix(current.Value.UpdatedAt)
                );
            }
            if (current.Value.Version != expectedVersion)
            {
                throw new LedgerConflictException(
                    $"Document '{key}' in run '{runId:N}' is at version '{current.Value.Version}', not '{expectedVersion}'."
                );
            }
        }
        else if (expectedVersion != 0)
        {
            throw new LedgerConflictException(
                $"Document '{key}' in run '{runId:N}' does not exist at version '{expectedVersion}'."
            );
        }

        var version = expectedVersion + 1;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO run_documents (run_id, key, version, payload, payload_hash, updated_at)
            VALUES ($run_id, $key, $version, $payload, $hash, $updated_at)
            ON CONFLICT (run_id, key) DO UPDATE SET
                version = excluded.version,
                payload = excluded.payload,
                payload_hash = excluded.payload_hash,
                updated_at = excluded.updated_at
            WHERE run_documents.version = $expected_version;
            """;
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$updated_at", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$expected_version", expectedVersion);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new LedgerConflictException($"Document '{key}' changed during its update.");
        }
        return new LedgerDocumentValue<TDocument>(version, value, now);
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

    private async ValueTask EnsureContractAsync(
        string storageName,
        string storageKind,
        string contractName,
        int contractVersion,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenAsync(cancellationToken);
        await EnsureContractAsync(
            connection,
            null,
            storageName,
            storageKind,
            contractName,
            contractVersion,
            cancellationToken
        );
    }

    private static async ValueTask EnsureContractAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string storageName,
        string storageKind,
        string contractName,
        int contractVersion,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ledger_contracts (storage_name, storage_kind, contract_name, contract_version)
            VALUES ($storage_name, $storage_kind, $contract_name, $contract_version)
            ON CONFLICT (storage_name) DO NOTHING;
            SELECT storage_kind, contract_name, contract_version
            FROM ledger_contracts
            WHERE storage_name = $storage_name;
            """;
        command.Parameters.AddWithValue("$storage_name", storageName);
        command.Parameters.AddWithValue("$storage_kind", storageKind);
        command.Parameters.AddWithValue("$contract_name", contractName);
        command.Parameters.AddWithValue("$contract_version", contractVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Ledger contract '{storageName}' was not registered."
            );
        }
        if (
            !string.Equals(reader.GetString(0), storageKind, StringComparison.Ordinal)
            || !string.Equals(reader.GetString(1), contractName, StringComparison.Ordinal)
            || reader.GetInt32(2) != contractVersion
        )
        {
            throw new LedgerConflictException(
                $"Storage name '{storageName}' is already registered as "
                    + $"'{reader.GetString(0)}:{reader.GetString(1)}@{reader.GetInt32(2)}'."
            );
        }
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
        string sql,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? throw new InvalidOperationException("SQLite returned no scalar value.")
            : (T)Convert.ChangeType(value, typeof(T));
    }

    private async ValueTask<LedgerRun> ReadRunAsync(
        SqliteConnection connection,
        Guid runId,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
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

    private async ValueTask<EntryRow?> ReadEntryByIdAsync<TEntry>(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid runId,
        string entryId,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT stream, sequence, payload, payload_hash, recorded_at FROM run_entries WHERE run_id = $run_id AND entry_id = $entry_id;";
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$entry_id", entryId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new EntryRow(
                reader.GetString(0),
                reader.GetInt64(1),
                (byte[])reader[2],
                (byte[])reader[3],
                reader.GetInt64(4)
            )
            : null;
    }

    private async ValueTask<LedgerDocumentValue<TDocument>?> ReadDocumentAsync<TDocument>(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid runId,
        string key,
        CancellationToken cancellationToken
    )
    {
        var row = await ReadDocumentRowAsync(
            connection,
            transaction,
            runId,
            key,
            cancellationToken
        );
        return row is null
            ? null
            : new LedgerDocumentValue<TDocument>(
                row.Value.Version,
                Deserialize<TDocument>(row.Value.Payload),
                FromUnix(row.Value.UpdatedAt)
            );
    }

    private static async ValueTask<DocumentRow?> ReadDocumentRowAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid runId,
        string key,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT version, payload, payload_hash, updated_at FROM run_documents WHERE run_id = $run_id AND key = $key;";
        command.Parameters.AddWithValue("$run_id", runId.ToString("N"));
        command.Parameters.AddWithValue("$key", key);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DocumentRow(
                reader.GetInt64(0),
                (byte[])reader[1],
                (byte[])reader[2],
                reader.GetInt64(3)
            )
            : null;
    }

    private T Deserialize<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, _serializerOptions)
        ?? throw new JsonException($"Ledger payload for '{typeof(T).FullName}' was null.");

    private DateTimeOffset Now() => FromUnix(_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

    private static DateTimeOffset FromUnix(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private readonly record struct EntryRow(
        string Stream,
        long Sequence,
        byte[] Payload,
        byte[] Hash,
        long RecordedAt
    );

    private readonly record struct DocumentRow(
        long Version,
        byte[] Payload,
        byte[] Hash,
        long UpdatedAt
    );

    private sealed record TransactionScope(
        SqliteConnection Connection,
        SqliteTransaction Transaction
    );
}
