using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tandem.Advanced;
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
        await using (var run = connection.CreateCommand())
        {
            run.CommandText = "SELECT EXISTS(SELECT 1 FROM runs WHERE run_id = $run_id)";
            run.Parameters.AddWithValue("$run_id", parsedRunId.ToString("N"));
            if (Convert.ToInt64(await run.ExecuteScalarAsync()) == 0)
            {
                throw new KeyNotFoundException($"Run '{runId}' does not exist.");
            }
        }
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
        Func<string, string, string, string> invokeSyncCallback,
        Func<string, string, string, CancellationToken, Task<string>> invokeAsyncCallback,
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
        using var terminalCancellation =
            definition.Presentation == "terminal"
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : null;
        var runCancellationToken = terminalCancellation?.Token ?? cancellationToken;
        var callbacks = new CallbackDispatcher(
            context,
            invokeSyncCallback,
            invokeAsyncCallback,
            runCancellationToken
        );
        var nodes = new Dictionary<string, RegisteredParticipant>(StringComparer.Ordinal);
        foreach (var node in definition.Nodes!)
        {
            nodes.Add(
                node.Id!,
                await RegisteredParticipantFactory.CreateAsync(
                    node,
                    callbacks,
                    runCancellationToken
                )
            );
        }
        var builder = RegisteredRouteRegistration.Start(nodes[definition.Start!], definition.Name!);
        ApplyPersistence(builder, definition, nodes.Values);
        foreach (var route in definition.Routes!)
        {
            RegisteredRouteRegistration.Add(builder, nodes, route, callbacks);
        }

        var pipeline = builder.Build(
            definition.Outputs!.Select(id => ((RegisteredTerminal)nodes[id]).Terminal).ToArray()
        );
        var handlers = new PipelineInteractionHandlers();
        foreach (var binding in definition.InteractionHandlers ?? [])
        {
            var participant = (RegisteredInteraction)nodes[binding.Target!];
            handlers.Handle(
                participant.Interaction,
                (request, token) =>
                    new(callbacks.InvokeAsync(binding.HandleCallback!, "", request.Request, token))
            );
        }

        var runId = Guid.CreateVersion7();
        var modelNames = definition
            .Nodes.SelectMany(EnumerateNodeContracts)
            .Where(node => node.Kind == "agent")
            .ToDictionary(node => node.Id!, node => node.Client!.Model!, StringComparer.Ordinal);
        await using var presentation = terminalCancellation is null
            ? null
            : new TerminalRunPresentation(
                pipeline.Inspect(),
                runId,
                terminalCancellation,
                modelNames
            );
        IPipelinePersistenceObserver? observer = null;
        SqliteLedgerStore? store = null;
        if (definition.LedgerPath is not null)
        {
            store = new SqliteLedgerStore(definition.LedgerPath);
            observer = await store.CreateObserverAsync(runId, pipeline, runCancellationToken);
        }
        var liveObserver = definition.ObservationCallback is null
            ? null
            : new RegisteredObservationObserver(callbacks, definition.ObservationCallback);
        var runObserver = RegisteredRunObserver.Compose(
            observer,
            liveObserver,
            presentation?.Observer
        );
        LedgerRunStatus? terminalStatus = null;
        string? terminalSummary = null;
        var preserveActiveFailure = false;
        try
        {
            if (presentation is not null)
            {
                await presentation.StartAsync(runCancellationToken);
            }
            var options = new PipelineRunOptions(
                RunId: runId,
                Interactions: handlers,
                Observer: runObserver
            );
            if (store is not null)
            {
                options = options.WithAcceptanceUnitOfWork(new LedgerAcceptanceUnitOfWork(store));
            }
            var result = await new PipelineRunner().RunAsync(
                pipeline,
                new JavaScriptState(definition.InitialState!),
                options,
                runCancellationToken
            );
            terminalStatus = result.Succeeded ? LedgerRunStatus.Ready : LedgerRunStatus.Failed;
            terminalSummary = result.Outcome?.Summary;
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
        catch (CallbackContractException exception)
        {
            terminalStatus = LedgerRunStatus.Faulted;
            terminalSummary = exception.Message;
            preserveActiveFailure = true;
            throw CallbackContractFailure(exception, exception);
        }
        catch (PipelineRunException exception)
        {
            terminalStatus = LedgerRunStatus.Faulted;
            terminalSummary = exception.InnerException?.Message ?? exception.Message;
            preserveActiveFailure = true;
            if (FindCallbackContractException(exception) is { } contract)
            {
                throw CallbackContractFailure(contract, exception);
            }
            throw new InvalidOperationException(
                exception.InnerException?.ToString() ?? exception.ToString(),
                exception
            );
        }
        catch (OperationCanceledException)
        {
            terminalStatus = LedgerRunStatus.Cancelled;
            terminalSummary = "Pipeline cancelled";
            preserveActiveFailure = true;
            throw;
        }
        catch (Exception exception)
        {
            terminalStatus = LedgerRunStatus.Faulted;
            terminalSummary = exception.Message;
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
            if (presentation is not null && terminalStatus is { } presentationStatus)
            {
                await presentation.CompleteAsync(
                    presentationStatus,
                    terminalSummary,
                    preserveActiveFailure
                );
            }
        }
    }

    private static CallbackContractException? FindCallbackContractException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is CallbackContractException contract)
                return contract;
        }
        return null;
    }

    private static InvalidOperationException CallbackContractFailure(
        CallbackContractException contract,
        Exception cause
    ) =>
        new(
            "TANDEM_CALLBACK_CONTRACT:"
                + JsonSerializer.Serialize(
                    new { boundary = contract.Boundary, problems = contract.Problems },
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                ),
            cause
        );

    private sealed class LedgerAcceptanceUnitOfWork(SqliteLedgerStore store)
        : IPipelineAcceptanceUnitOfWork
    {
        public ValueTask<T> ExecuteAsync<T>(
            Func<CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken
        ) => store.ExecuteAsync(operation, cancellationToken);
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

        foreach (
            var participant in Flatten(participants)
                .Where(node => node.Contract.Persist is not null)
        )
        {
            if (participant is RegisteredInteraction interaction)
            {
                if (participant.Contract.Persist!.Value)
                {
                    builder.Persist(interaction.Interaction);
                }
                else
                {
                    builder.DoNotPersist(interaction.Interaction);
                }
            }
            else if (participant.Contract.Persist!.Value)
            {
                builder.Persist(PersistableNode(participant));
            }
            else
            {
                builder.DoNotPersist(PersistableNode(participant));
            }
        }
    }

    private static IEnumerable<RegisteredParticipant> Flatten(
        IEnumerable<RegisteredParticipant> participants
    )
    {
        foreach (var participant in participants)
        {
            yield return participant;
            if (participant is RegisteredStandard standard)
            {
                foreach (var owned in Flatten(standard.Owned))
                    yield return owned;
            }
        }
    }

    private static IEnumerable<RegisteredNodeContract> EnumerateNodeContracts(
        RegisteredNodeContract node
    )
    {
        yield return node;
        foreach (var branch in node.Branches ?? [])
        {
            if (branch.Participant is not null)
            {
                yield return branch.Participant;
            }
        }
    }

    private static IPipelineNode<JavaScriptState> PersistableNode(
        RegisteredParticipant participant
    ) =>
        participant switch
        {
            RegisteredStage stage => stage.Stage,
            RegisteredStandard standard => standard.Standard,
            RegisteredTerminal terminal => terminal.Terminal,
            _ => throw new InvalidOperationException(
                "Interaction persistence must be applied to the interaction definition."
            ),
        };
}
