using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tandem.Ledger;

namespace Tandem.NodeApiSpike;

public static partial class NodePipelineBridge
{
    /// <summary>Reads accepted semantic journal values from a packaged SQLite ledger.</summary>
    public static async Task<string> InspectAcceptedAsync(string ledgerPath, string runId)
    {
        if (!Guid.TryParse(runId, out var parsedRunId))
        {
            throw new ArgumentException("runId must be a GUID.", nameof(runId));
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(ledgerPath),
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT payload FROM run_entries WHERE run_id = $run_id ORDER BY sequence";
        command.Parameters.AddWithValue("$run_id", parsedRunId.ToString("N"));
        await using var reader = await command.ExecuteReaderAsync();
        var accepted = new List<object>();
        while (await reader.ReadAsync())
        {
            var record = JsonSerializer.Deserialize<RuntimeJournalRecord>(
                (byte[])reader[0],
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            );
            if (record is not null && PipelineJournal.IsAccepted(record))
            {
                accepted.Add(
                    new
                    {
                        kind = record.Kind.ToString(),
                        record.StepId,
                        record.ValueType,
                        record.Payload,
                    }
                );
            }
        }
        return JsonSerializer.Serialize(accepted);
    }

    /// <summary>Registers and runs a complete JavaScript-authored Tandem graph.</summary>
    public static async Task<string> RunRegisteredGraphAsync(
        string definitionJson,
        Func<string, string, string, Task<string>> invokeCallback,
        CancellationToken cancellationToken = default
    )
    {
        PreloadDependencies();
        var context =
            SynchronizationContext.Current
            ?? throw new InvalidOperationException(
                "A JavaScript synchronization context is required."
            );
        var definition = RegistrationContractValidator.ParseAndValidate(definitionJson);
        var callbacks = new CallbackDispatcher(context, invokeCallback);
        var nodes = new Dictionary<string, RegisteredParticipant>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes!)
        {
            nodes.Add(node.Id!, await RegisteredParticipantFactory.CreateAsync(node, callbacks));
        }
        var builder = RegisteredRouteRegistration.Start(nodes[definition.Start!], definition.Name!);
        ApplyPersistence(builder, definition, nodes.Values);
        foreach (var route in definition.Routes!)
        {
            RegisteredRouteRegistration.Add(builder, nodes, route, callbacks);
        }

        var pipeline = builder.Build(definition.Outputs!.Select(id => nodes[id].Node).ToArray());
        var handlers = new PipelineInteractionHandlers();
        foreach (var participant in nodes.Values.Where(node => node.Interaction is not null))
        {
            handlers.Handle(
                participant.Interaction!,
                (request, _) =>
                    new(
                        callbacks.InvokeAsync(
                            participant.Contract.HandleCallback!,
                            "",
                            request.Request
                        )
                    )
            );
        }

        var runId = Guid.CreateVersion7();
        IPipelinePersistenceObserver? observer = null;
        SqliteLedgerStore? store = null;
        if (definition.LedgerPath is not null)
        {
            store = new SqliteLedgerStore(definition.LedgerPath);
            observer = await store.CreateObserverAsync(runId, pipeline, cancellationToken);
        }
        LedgerRunStatus? terminalStatus = null;
        var preserveActiveFailure = false;
        try
        {
            var result = await new PipelineRunner().RunAsync(
                pipeline,
                new JavaScriptState(definition.InitialState!),
                new PipelineRunOptions(RunId: runId, Interactions: handlers, Observer: observer),
                cancellationToken
            );
            terminalStatus = result.Succeeded ? LedgerRunStatus.Ready : LedgerRunStatus.Failed;
            return JsonSerializer.Serialize(
                new
                {
                    runId,
                    succeeded = result.Succeeded,
                    state = JsonDocument.Parse(result.State.Json).RootElement,
                    summary = result.Outcome?.Summary,
                }
            );
        }
        catch (PipelineRunException exception)
        {
            terminalStatus = LedgerRunStatus.Faulted;
            preserveActiveFailure = true;
            throw new InvalidOperationException(
                exception.InnerException?.ToString() ?? exception.ToString(),
                exception
            );
        }
        catch (OperationCanceledException)
        {
            terminalStatus = LedgerRunStatus.Cancelled;
            preserveActiveFailure = true;
            throw;
        }
        catch
        {
            terminalStatus = LedgerRunStatus.Faulted;
            preserveActiveFailure = true;
            throw;
        }
        finally
        {
            if (store is not null && terminalStatus is { } status)
            {
                try
                {
                    await store.CompleteRunAsync(runId, status, CancellationToken.None);
                }
                catch when (preserveActiveFailure)
                {
                    // Preserve the active run failure when best-effort terminalization also fails.
                }
            }
        }
    }

    private static void ApplyPersistence(
        PipelineBuilder<JavaScriptState> builder,
        RegisteredGraphContract graph,
        IEnumerable<RegisteredParticipant> participants
    )
    {
        if (graph.Persist)
        {
            builder.Persist();
        }

        foreach (var participant in participants.Where(node => node.Contract.Persist is not null))
        {
            if (participant.Interaction is not null)
            {
                if (participant.Contract.Persist!.Value)
                {
                    builder.Persist(participant.Interaction);
                }
                else
                {
                    builder.DoNotPersist(participant.Interaction);
                }
            }
            else if (participant.Contract.Persist!.Value)
            {
                builder.Persist(participant.Node);
            }
            else
            {
                builder.DoNotPersist(participant.Node);
            }
        }
    }
}
