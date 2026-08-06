using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tandem.Domain;

namespace Tandem.Infrastructure.Lifecycle;

[McpServerToolType]
public sealed class LifecycleMcpTools(
    LifecycleReceiptStore receiptStore,
    LifecycleToolContext context
)
{
    [McpServerTool]
    [Description(
        "Ask the planner block for guidance. Ends the current turn. Returns the accepted planner request outcome."
    )]
    public async Task<CallToolResult> ask_planner(
        [Description("The question for the planner.")] string question,
        [Description("The proposed approach to implement.")] string proposedApproach,
        [Description(
            "Evidence paths supporting the question or approach (file paths, line references)."
        )]
            string[] evidence,
        CancellationToken cancellationToken
    )
    {
        var request = new AskPlannerRequest(question, proposedApproach, evidence);
        return await HandleAskPlannerAsync(request, cancellationToken);
    }

    internal async Task<CallToolResult> HandleAskPlannerAsync(
        AskPlannerRequest request,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                question = request.Question,
                proposedApproach = request.ProposedApproach,
                evidence = request.Evidence,
            }
        );
        return await AcceptAsync(
            OutcomeKinds.PlannerRequested,
            $"Planner asked: {request.Question}",
            payload,
            cancellationToken
        );
    }

    [McpServerTool]
    [Description(
        "Submit the implementation report. Ends the current turn. Returns the accepted report outcome."
    )]
    public async Task<CallToolResult> submit_report(
        [Description("Summary of the implementation work.")] string summary,
        [Description("Outcome claims asserted by the report.")] string[] outcomes,
        [Description("Evidence supporting the outcome claims (file paths, line references).")]
            string[] evidence,
        CancellationToken cancellationToken
    )
    {
        var request = new SubmitReportRequest(summary, outcomes, evidence);
        return await HandleSubmitReportAsync(request, cancellationToken);
    }

    internal async Task<CallToolResult> HandleSubmitReportAsync(
        SubmitReportRequest request,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                summary = request.Summary,
                outcomes = request.Outcomes,
                evidence = request.Evidence,
            }
        );
        return await AcceptAsync(
            OutcomeKinds.ReportSubmitted,
            $"Report submitted: {request.Summary}",
            payload,
            cancellationToken
        );
    }

    [McpServerTool]
    [Description(
        "Write a checkpoint of current work state. Ends the current turn. Returns the accepted checkpoint outcome."
    )]
    public async Task<CallToolResult> write_checkpoint(
        [Description("Summary of the checkpoint.")] string summary,
        [Description("Work that has been completed so far.")] string[] completed,
        [Description("Work that remains to be done next.")] string[] next,
        CancellationToken cancellationToken
    )
    {
        var request = new WriteCheckpointRequest(summary, completed, next);
        return await HandleWriteCheckpointAsync(request, cancellationToken);
    }

    internal async Task<CallToolResult> HandleWriteCheckpointAsync(
        WriteCheckpointRequest request,
        CancellationToken cancellationToken
    )
    {
        var payload = JsonSerializer.SerializeToElement(
            new
            {
                summary = request.Summary,
                completed = request.Completed,
                next = request.Next,
            }
        );
        return await AcceptAsync(
            OutcomeKinds.CheckpointWritten,
            $"Checkpoint written: {request.Summary}",
            payload,
            cancellationToken
        );
    }

    private async Task<CallToolResult> AcceptAsync(
        string kind,
        string summary,
        JsonElement payload,
        CancellationToken cancellationToken
    )
    {
        var existing = await receiptStore.ReadAsync(
            context.RunId,
            context.InvocationId,
            cancellationToken
        );
        LifecycleReceipt receipt;
        if (existing is not null)
        {
            if (!SameOutcome(existing, kind, summary, payload))
            {
                return Result(
                    new
                    {
                        error = "conflicting lifecycle outcome",
                        invocationId = context.InvocationId,
                        blockId = context.BlockId,
                        problems = new[]
                        {
                            new
                            {
                                field = "$",
                                message = "A different lifecycle outcome is already accepted for this invocation.",
                            },
                        },
                    },
                    isError: true
                );
            }
            receipt = existing;
        }
        else
        {
            receipt = await receiptStore.WriteAsync(
                context.RunId,
                context.InvocationId,
                context.BlockId,
                kind,
                summary,
                payload,
                cancellationToken
            );
        }

        return Result(
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
            },
            isError: false
        );
    }

    private static CallToolResult Result(object payload, bool isError)
    {
        var json = JsonSerializer.Serialize(payload);
        return new CallToolResult
        {
            IsError = isError,
            Content = [new TextContentBlock { Text = json }],
            StructuredContent = JsonSerializer.SerializeToElement(payload),
        };
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
