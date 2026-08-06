using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tandem.Infrastructure.Lifecycle;

public static class DebateMcpRegistration
{
    public static IMcpServerBuilder AddDebateMcpTools(this IServiceCollection services) =>
        services.AddMcpServer().WithTools<DebateMcpTools>();
}

[McpServerToolType]
public sealed class DebateMcpTools(LifecycleReceiptStore receiptStore, LifecycleToolContext context)
{
    [McpServerTool]
    [Description("Submit the accepted debate verdict and end the judge turn.")]
    public async Task<CallToolResult> submit_verdict(
        [Description("The final verdict.")] string verdict,
        [Description("The reason supporting the verdict.")] string reason,
        CancellationToken cancellationToken
    )
    {
        if (string.IsNullOrWhiteSpace(verdict) || string.IsNullOrWhiteSpace(reason))
        {
            return Result(
                new
                {
                    error = "invalid submit_verdict call",
                    problems = new[] { "verdict and reason are required" },
                },
                true
            );
        }

        var payload = JsonSerializer.SerializeToElement(new { verdict, reason });
        const string kind = "debate.verdict.submitted";
        var summary = $"Verdict submitted: {verdict}";
        var accepted = await receiptStore.CreateOrReadAsync(
            context.RunId,
            context.InvocationId,
            context.BlockId,
            kind,
            summary,
            payload,
            cancellationToken
        );
        var receipt = accepted.Receipt;
        if (
            receipt.Kind != kind
            || receipt.Summary != summary
            || !JsonElement.DeepEquals(receipt.Payload, payload)
        )
        {
            return Result(new { error = "conflicting lifecycle outcome" }, true);
        }

        return Result(
            new
            {
                accepted = true,
                invocationId = receipt.InvocationId,
                outcome = new { kind = receipt.Kind, payload = receipt.Payload },
            },
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
