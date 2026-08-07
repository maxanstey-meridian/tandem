using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tandem.Actions;

namespace Tandem.Sample.Debate;

[McpServerToolType]
public sealed class SubmitVerdictAction(
    LifecycleReceiptStore receipts,
    LifecycleToolContext context
)
{
    public const string ToolName = "submit_verdict";
    public const string OutcomeKind = "debate.verdict.submitted";

    [McpServerTool(Name = ToolName)]
    [Description("Submit the final debate verdict and end the judge turn.")]
    public async Task<CallToolResult> SubmitAsync(
        string verdict,
        string reason,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(verdict) || string.IsNullOrWhiteSpace(reason))
        {
            return Result(new { error = "invalid submit_verdict call" }, true);
        }
        var payload = JsonSerializer.SerializeToElement(new { verdict, reason });
        var summary = $"Verdict submitted: {verdict}";
        var accepted = await receipts.CreateOrReadAsync(
            context.RunId,
            context.InvocationId,
            context.BlockId,
            OutcomeKind,
            summary,
            payload,
            cancellationToken
        );
        var receipt = accepted.Receipt;
        if (
            receipt.Kind != OutcomeKind
            || receipt.Summary != summary
            || !JsonElement.DeepEquals(receipt.Payload, payload)
        )
        {
            return Result(new { error = "conflicting lifecycle outcome" }, true);
        }
        return Result(
            new { accepted = true, outcome = new { kind = receipt.Kind, payload } },
            false
        );
    }

    private static CallToolResult Result(object payload, bool isError) =>
        new()
        {
            IsError = isError,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(payload) }],
            StructuredContent = JsonSerializer.SerializeToElement(payload),
        };
}
