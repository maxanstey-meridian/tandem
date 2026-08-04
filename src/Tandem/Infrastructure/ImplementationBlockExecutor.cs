using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Tandem.Domain;

#pragma warning disable MAAI001

namespace Tandem.Infrastructure;

public sealed class ImplementationBlockExecutor : Executor<RunContext, BlockResult>
{
    private const int TurnTimeoutMs = 600_000;
    private readonly Func<ResolvedProfile, IChatClient> _chatClientFactory;

    public ImplementationBlockExecutor(string apiKey)
        : this(profile => new ChatClientBuilder().Build(profile, apiKey)) { }

    internal ImplementationBlockExecutor(Func<ResolvedProfile, IChatClient> chatClientFactory)
        : base("implementation")
    {
        _chatClientFactory = chatClientFactory;
    }

    public override async ValueTask<BlockResult> HandleAsync(
        RunContext input,
        IWorkflowContext context,
        CancellationToken cancellationToken
    )
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TurnTimeoutMs);

        var chatClient = _chatClientFactory(input.Profile);
        var fileStore = new GitExcludedFileStore(new FileSystemAgentFileStore(input.WorkspacePath));

        var agent = new HarnessAgent(
            chatClient,
            new HarnessAgentOptions
            {
                Id = "implementation",
                Name = "Implementation",
                HarnessInstructions = "",
                ChatOptions = new ChatOptions
                {
                    Instructions = """
                    You are the implementation block in Tandem.

                    Work only inside the provided workspace using the available file tools.
                    Inspect relevant files before editing. Implement the packet outcomes while
                    respecting its constraints. Do not use prose as a substitute for making the
                    requested changes.

                    When the work is complete, briefly state what changed. This first slice has no
                    planner, reviewer, verification block, shell tool, or lifecycle MCP tools.
                    """,
                },
                DisableFileMemory = true,
                DisableTodoProvider = true,
                DisableAgentModeProvider = true,
                DisableAgentSkillsProvider = true,
                DisableWebSearch = true,
                DisableToolAutoApproval = true,
                DisableOpenTelemetry = true,
                DisableCompaction = true,
                MaximumIterationsPerRequest = 100,
                FileAccessStore = fileStore,
                FileAccessProviderOptions = new FileAccessProviderOptions
                {
                    DisableReadOnlyToolApproval = true,
                    DisableWriteToolApproval = true,
                },
            }
        );

        var session = await agent.CreateSessionAsync(cts.Token);
        var userMessage = BuildUserMessage(input);

        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync(userMessage, session, null, cts.Token))
        {
            updates.Add(update);
            await context.AddEventAsync(
                new AgentResponseUpdateEvent("implementation", update),
                cancellationToken
            );
        }

        var response = updates.ToAgentResponse();
        return new BlockResult(response.Text, input.Profile.Model, input.WorkspacePath);
    }

    private static string BuildUserMessage(RunContext context)
    {
        var packet = context.Packet;
        var outcomes = string.Join(
            "\n",
            packet.Outcomes.Select(o => $"- [{o.Id}] {o.Description}")
        );
        var constraints =
            packet.Constraints.Count > 0
                ? string.Join("\n", packet.Constraints.Select(c => $"- {c}"))
                : "(none)";
        var implContext = string.IsNullOrWhiteSpace(packet.ImplementationContext)
            ? "(none)"
            : packet.ImplementationContext;

        return $"""
            Packet: {packet.Title}
            Workspace: {context.WorkspacePath}
            Pinned base: {context.PinnedBaseSha}

            Outcomes:
            {outcomes}

            Constraints:
            {constraints}

            Implementation context:
            {implContext}

            Inspect the workspace and implement the outcomes now.
            """;
    }
}

#pragma warning restore MAAI001
