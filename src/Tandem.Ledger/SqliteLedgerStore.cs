using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Tandem.Ledger;

public sealed class SqliteLedgerStore
{
    private const int SchemaVersion = 2;
    private const string SelectJournal =
        "SELECT id AS Id, sequence AS Sequence, record AS Record, recorded_at AS RecordedAtMilliseconds FROM journal";
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
            kind TEXT GENERATED ALWAYS AS (json_extract(record, '$.kind')) VIRTUAL,
            step_id TEXT GENERATED ALWAYS AS (json_extract(record, '$.stepId')) VIRTUAL,
            identity TEXT GENERATED ALWAYS AS (json_extract(record, '$.identity')) VIRTUAL,
            value_type TEXT GENERATED ALWAYS AS (json_extract(record, '$.valueType')) VIRTUAL,
            payload_type TEXT GENERATED ALWAYS AS (json_type(record, '$.payload')) VIRTUAL,
            -- An accepted value: the fact a participant produced or a human supplied.
            accepted INTEGER GENERATED ALWAYS AS (
                kind IN ('StructuredOutputAccepted', 'CapabilityAccepted', 'InteractionRequested', 'InteractionAnswered', 'StepCompleted')
                AND (coalesce(payload_type, 'null') <> 'null' OR trim(coalesce(value_type, '')) <> '')
            ) VIRTUAL,
            -- What the ledger tools show agents: accepted values and captured command output.
            agent_readable INTEGER GENERATED ALWAYS AS (
                accepted
                OR (kind = 'CommandCompleted' AND coalesce(payload_type, 'null') <> 'null')
                OR (kind = 'ActionCompleted' AND payload_type = 'object')
            ) VIRTUAL,
            UNIQUE (run_id, sequence)
        );
        CREATE INDEX journal_accepted ON journal (run_id, accepted, step_id, value_type);
        CREATE INDEX journal_actions ON journal (run_id, kind, step_id, identity);
        PRAGMA user_version = 2;
        """;

    internal static JsonSerializerOptions JournalJson { get; } = TandemJson.CreateTypedContract();

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
        var payload = JsonSerializer.Serialize(record, JournalJson);
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
    ) =>
        (await ReadJournalRowsAsync(runId, "ORDER BY id", new { }, cancellationToken))
            .Select(ToEntry)
            .ToList();

    public async ValueTask<IReadOnlyList<RuntimeJournalRecord>> ReadAcceptedAsync(
        Guid runId,
        CancellationToken cancellationToken = default
    ) =>
        (await ReadJournalRowsAsync(runId, "AND accepted ORDER BY id", new { }, cancellationToken))
            .Select(Deserialize)
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
        var latestRow = (
            await ReadJournalRowsAsync(
                runId,
                stepId is null
                    ? "AND accepted AND value_type = @ValueType ORDER BY id DESC LIMIT 1"
                    : "AND accepted AND step_id = @StepId ORDER BY id DESC LIMIT 1",
                new { StepId = stepId, ValueType = expectedType },
                cancellationToken
            )
        ).SingleOrDefault();
        if (latestRow is null)
        {
            return null;
        }
        var latest = ToEntry(latestRow);

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
                payload.Deserialize<TValue>(JournalJson)
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

    internal async ValueTask<IReadOnlyList<JournalRow>> ReadAgentReadableAsync(
        Guid runId,
        long afterId,
        string? valueText,
        int limit,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        // Matches JSON values, never property names. SQLite's lower() folds ASCII only.
        var rows = await connection.QueryAsync<JournalRow>(
            new CommandDefinition(
                $"""
                {SelectJournal}
                WHERE run_id = @RunId AND id > @AfterId AND agent_readable
                    AND (@ValueText IS NULL OR EXISTS (
                        SELECT 1 FROM json_tree(journal.record)
                        WHERE atom IS NOT NULL AND instr(lower(atom), lower(@ValueText)) > 0
                    ))
                ORDER BY id
                LIMIT @Limit;
                """,
                new
                {
                    RunId = Key(runId),
                    AfterId = afterId,
                    ValueText = valueText,
                    Limit = limit,
                },
                cancellationToken: cancellationToken
            )
        );
        return rows.AsList();
    }

    internal async ValueTask<JournalRecordRow?> ReadRecordAsync(
        Guid runId,
        long id,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<JournalRecordRow>(
            new CommandDefinition(
                "SELECT record AS Record, agent_readable AS AgentReadable FROM journal WHERE run_id = @RunId AND id = @Id;",
                new { RunId = Key(runId), Id = id },
                cancellationToken: cancellationToken
            )
        );
    }

    internal async ValueTask<long?> FindActionEntryAsync(
        Guid runId,
        string stepId,
        string invocationId,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<long?>(
            new CommandDefinition(
                """
                SELECT id FROM journal
                WHERE run_id = @RunId AND kind = 'ActionCompleted' AND step_id = @StepId
                    AND identity = @Identity AND coalesce(payload_type, 'null') <> 'null'
                ORDER BY id DESC
                LIMIT 1;
                """,
                new
                {
                    RunId = Key(runId),
                    StepId = stepId,
                    Identity = invocationId,
                },
                cancellationToken: cancellationToken
            )
        );
    }

    private async ValueTask<IEnumerable<JournalRow>> ReadJournalRowsAsync(
        Guid runId,
        string conditionAndOrder,
        object parameters,
        CancellationToken cancellationToken
    )
    {
        await using var connection = await OpenReadOnlyAsync(cancellationToken);
        await ReadRunAsync(connection, null, runId, cancellationToken);
        var runParameters = new DynamicParameters(parameters);
        runParameters.Add("RunId", Key(runId));
        return await connection.QueryAsync<JournalRow>(
            new CommandDefinition(
                $"{SelectJournal} WHERE run_id = @RunId {conditionAndOrder};",
                runParameters,
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
        new(row.Sequence, Deserialize(row), row.RecordedAt);

    private static RuntimeJournalRecord Deserialize(JournalRow row)
    {
        try
        {
            return JsonSerializer.Deserialize<RuntimeJournalRecord>(row.Record, JournalJson)
                ?? throw new JsonException("Journal record is null.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new LedgerDataException($"Journal record '{row.Id}' is malformed.", exception);
        }
    }

    private static string Key(Guid runId) => runId.ToString("N");

    private static long NowMilliseconds() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    internal static DateTimeOffset FromUnix(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private sealed record RunRow(
        string Composition,
        string Status,
        long StartedAt,
        long UpdatedAt,
        long? EndedAt
    );
}

internal sealed record JournalRow(
    long Id,
    long Sequence,
    string Record,
    long RecordedAtMilliseconds
)
{
    public DateTimeOffset RecordedAt => SqliteLedgerStore.FromUnix(RecordedAtMilliseconds);
}

internal sealed record JournalRecordRow(string Record, long? AgentReadable)
{
    public bool IsAgentReadable => AgentReadable == 1;
}
