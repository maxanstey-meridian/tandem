using System.Runtime.Loader;
using System.Text.Json;
using Tandem.Advanced;
using Tandem.Ledger;
using Tandem.Terminal;

namespace Tandem.Bridge;

public static partial class NodePipelineBridge
{
    /// <summary>Reads accepted semantic journal values from a packaged SQLite ledger.</summary>
    public static Task<string> InspectAcceptedAsync(string ledgerPath, string runId)
    {
        if (!Guid.TryParse(runId, out var parsedRunId))
        {
            throw new ArgumentException("runId must be a GUID.", nameof(runId));
        }
        PreloadDependencies();
        return InspectAcceptedCoreAsync(ledgerPath, parsedRunId);
    }

    private static async Task<string> InspectAcceptedCoreAsync(string ledgerPath, Guid runId)
    {
        var records = await new SqliteLedgerStore(ledgerPath).ReadAcceptedAsync(runId);
        var accepted = records.Select(record => new
        {
            kind = record.Kind.ToString(),
            record.StepId,
            record.VisitId,
            record.ValueType,
            record.Payload,
        });
        return JsonSerializer.Serialize(accepted, TandemJson.CreateTypedContract());
    }

    /// <summary>
    /// Registers and runs a complete JavaScript-authored Tandem graph and reports how it ended as a
    /// <see cref="RunEnvelope"/>. Once a run is attempted every failure is reported, because a fault
    /// raised inside the run cannot be told apart from a bridge defect; a fault carries the full
    /// exception text. Only a caller without a JavaScript synchronization context gets an exception.
    /// </summary>
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
        var runId = Guid.CreateVersion7();
        // The terminal cancels the run itself as well as through the caller's token.
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        try
        {
            var definition = RegistrationContractValidator.ParseAndValidate(definitionJson);
            var callbacks = new CallbackDispatcher(
                context,
                invokeSyncCallback,
                invokeAsyncCallback,
                runCancellation.Token
            );
            // SQLite's async APIs perform synchronous I/O. A busy wait must not block
            // Node's thread while another run needs a JS callback to finish acceptance.
            // CallbackDispatcher already marshals authored callbacks to that thread.
            var result = await Task.Run(
                () =>
                    RunRegisteredGraphCoreAsync(
                        runId,
                        definition,
                        callbacks,
                        runCancellation,
                        cancellationToken
                    ),
                cancellationToken
            );
            return RunEnvelope.Completed(result).ToJson();
        }
        catch (Exception exception)
        {
            return RunEnvelope
                .Ended(runId, exception, runCancellation.IsCancellationRequested)
                .ToJson();
        }
    }

    private static async Task<PipelineRunResult<JavaScriptState>> RunRegisteredGraphCoreAsync(
        Guid runId,
        RegisteredGraphContract definition,
        CallbackDispatcher callbacks,
        CancellationTokenSource runCancellation,
        CancellationToken cancellationToken
    )
    {
        // MAF resolves interaction payload types by assembly-qualified name. Keep
        // that resolution in the bridge's load context when running off Node's thread.
        using var reflectionScope = AssemblyLoadContext.EnterContextualReflection(
            typeof(NodePipelineBridge).Assembly
        );
        var (pipeline, handlers) = BuildGraph(definition, callbacks);
        foreach (
            var agent in RegistrationContractValidator
                .EnumerateNodes(definition.Nodes)
                .OfType<AgentNodeContract>()
                .Where(agent => agent.Client.VerifyModel)
        )
        {
            await OpenAiCompatibleChatClients.VerifyModelAsync(agent.Client, runCancellation.Token);
        }

        SqliteLedgerStore? store = null;
        IPipelineObserver? persistence = null;
        if (definition.LedgerPath is not null)
        {
            store = new SqliteLedgerStore(definition.LedgerPath);
            persistence = await store.CreateObserverAsync(runId, pipeline, runCancellation.Token);
        }
        var options = new PipelineRunOptions(
            RunId: runId,
            Interactions: handlers,
            Observer: PipelineObservers.Compose(
                persistence,
                definition.ObservationCallback is null
                    ? null
                    : new RegisteredObservationObserver(callbacks, definition.ObservationCallback)
            )
        )
        {
            Ledger = definition.EnableLedgerTools ? store?.ForRun(runId) : null,
        };
        var runner = new PipelineRunner();
        var initialState = new JavaScriptState(definition.InitialState);
        if (definition.Presentation != RegisteredPresentation.Terminal)
        {
            return store is null
                ? await runner.RunAsync(pipeline, initialState, options, cancellationToken)
                : await store.RecordRunAsync(
                    runId,
                    () => runner.RunAsync(pipeline, initialState, options, cancellationToken)
                );
        }
        // The terminal records the ledger status before its display waits for the user.
        return await runner.RunWithTerminalAsync(
            pipeline,
            initialState,
            new TerminalPipelineRunOptions
            {
                Run = options,
                RunCancellation = runCancellation,
                Display = new TerminalDisplayOptions
                {
                    TruncatedToolNames = new HashSet<string>(
                        definition.Terminal?.TruncatedToolNames ?? [],
                        StringComparer.Ordinal
                    ),
                },
                TerminalizingAsync = store is null
                    ? null
                    : async (completion, token) =>
                        await store.CompleteRunAsync(
                            runId,
                            ToLedgerStatus(completion.Status),
                            token
                        ),
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Builds the graph through Tandem's builders, which own participant and route rules, and
    /// reports their failures as registration errors before any model preflight or ledger write.
    /// </summary>
    internal static (
        Pipeline<JavaScriptState> Pipeline,
        PipelineInteractionHandlers Handlers
    ) BuildGraph(RegisteredGraphContract definition, CallbackDispatcher callbacks)
    {
        var nodes = new Dictionary<string, RegisteredParticipant>(StringComparer.Ordinal);
        foreach (var (index, node) in definition.Nodes.Index())
        {
            nodes.Add(
                node.Id,
                RegistrationContractValidator.CoreRule(
                    $"nodes[{index}]",
                    () => RegisteredParticipantFactory.Create(node, callbacks)
                )
            );
        }
        var builder = RegistrationContractValidator.CoreRule(
            "start",
            () => RegisteredRouteRegistration.Start(nodes[definition.Start], definition.Name)
        );
        ApplyPersistence(builder, definition, nodes.Values);
        foreach (var (index, route) in definition.Routes.Index())
        {
            RegistrationContractValidator.CoreRule(
                $"routes[{index}]",
                () => RegisteredRouteRegistration.Add(builder, nodes, route, callbacks)
            );
        }
        var pipeline = RegistrationContractValidator.CoreRule(
            "outputs",
            () =>
                builder.Build(
                    definition
                        .Outputs.Select(id => ((RegisteredTerminal)nodes[id]).Terminal)
                        .ToArray()
                )
        );
        var handlers = new PipelineInteractionHandlers();
        foreach (var binding in definition.InteractionHandlers ?? [])
        {
            var participant = (RegisteredInteraction)nodes[binding.Target];
            handlers.Handle(
                participant.Interaction,
                (request, token) =>
                    new(callbacks.InvokeAsync(binding.HandleCallback, "", request.Request, token))
            );
        }
        return (pipeline, handlers);
    }

    private static LedgerRunStatus ToLedgerStatus(TerminalPipelineStatus status) =>
        status switch
        {
            TerminalPipelineStatus.Succeeded => LedgerRunStatus.Ready,
            TerminalPipelineStatus.Failed => LedgerRunStatus.Failed,
            TerminalPipelineStatus.Cancelled => LedgerRunStatus.Cancelled,
            _ => LedgerRunStatus.Faulted,
        };

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
            var children = participant switch
            {
                RegisteredStandard standard => standard.Owned,
                RegisteredStage stage => stage.Owned ?? [],
                _ => [],
            };
            foreach (var child in Flatten(children))
            {
                yield return child;
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
