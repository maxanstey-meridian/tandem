using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using Tandem.Domain;

namespace Tandem.Infrastructure.Lifecycle;

[McpServerToolType]
public sealed class LifecycleMcpTools
{
    private readonly LifecycleReceiptStore _receiptStore;
    private readonly LifecycleToolContext _context;

    public LifecycleMcpTools(LifecycleReceiptStore receiptStore, LifecycleToolContext context)
    {
        _receiptStore = receiptStore;
        _context = context;
    }

    [McpServerTool]
    [Description(
        "Ask the planner block for guidance. Ends the current turn. Returns the accepted planner request outcome."
    )]
    public async Task<string> ask_planner(
        [Description("The question for the planner.")] string question,
        [Description("The proposed approach to implement.")] string proposedApproach,
        [Description(
            "Evidence paths supporting the question or approach (file paths, line references)."
        )]
            string[] evidence,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                question,
                proposedApproach,
                evidence,
            }
        );
        return await AcceptAsync(
            OutcomeKinds.PlannerRequested,
            $"Planner asked: {question}",
            payload,
            cancellationToken
        );
    }

    [McpServerTool]
    [Description(
        "Submit the implementation report. Ends the current turn. Returns the accepted report outcome."
    )]
    public async Task<string> submit_report(
        [Description("Summary of the implementation work.")] string summary,
        [Description("Outcome claims asserted by the report.")] string[] outcomes,
        [Description("Evidence supporting the outcome claims (file paths, line references).")]
            string[] evidence,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                summary,
                outcomes,
                evidence,
            }
        );
        return await AcceptAsync(
            OutcomeKinds.ReportSubmitted,
            $"Report submitted: {summary}",
            payload,
            cancellationToken
        );
    }

    [McpServerTool]
    [Description(
        "Write a checkpoint of current work state. Ends the current turn. Returns the accepted checkpoint outcome."
    )]
    public async Task<string> write_checkpoint(
        [Description("Summary of the checkpoint.")] string summary,
        [Description("Work that has been completed so far.")] string[] completed,
        [Description("Work that remains to be done next.")] string[] next,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                summary,
                completed,
                next,
            }
        );
        return await AcceptAsync(
            OutcomeKinds.CheckpointWritten,
            $"Checkpoint written: {summary}",
            payload,
            cancellationToken
        );
    }

    private async Task<string> AcceptAsync(
        string kind,
        string summary,
        JsonElement payload,
        CancellationToken cancellationToken
    )
    {
        var existing = await _receiptStore.ReadAsync(
            _context.RunId,
            _context.InvocationId,
            cancellationToken
        );
        LifecycleReceipt receipt;
        if (existing is not null)
        {
            if (!SameOutcome(existing, kind, summary, payload))
            {
                return JsonSerializer.Serialize(
                    new
                    {
                        accepted = false,
                        invocationId = _context.InvocationId,
                        blockId = _context.BlockId,
                        error = "Conflicting lifecycle outcome already accepted for this invocation.",
                    }
                );
            }
            receipt = existing;
        }
        else
        {
            receipt = await _receiptStore.WriteAsync(
                _context.RunId,
                _context.InvocationId,
                _context.BlockId,
                kind,
                summary,
                payload,
                cancellationToken
            );
        }

        return JsonSerializer.Serialize(
            new
            {
                accepted = true,
                invocationId = receipt.InvocationId,
                blockId = receipt.BlockId,
                outcome = new
                {
                    kind = receipt.Kind,
                    summary = receipt.Summary,
                    payload = receipt.Payload,
                },
            }
        );
    }

    private static bool SameOutcome(
        LifecycleReceipt existing,
        string kind,
        string summary,
        JsonElement payload
    ) =>
        existing.Kind == kind
        && existing.Summary == summary
        && JsonElement.DeepEquals(existing.Payload, payload);
}
