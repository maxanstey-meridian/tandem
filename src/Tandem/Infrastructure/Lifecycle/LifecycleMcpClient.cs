using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Tandem.Infrastructure.Lifecycle;

public sealed class LifecycleMcpClient(
    string tandemHome,
    string tandemExePath,
    Guid runId,
    string blockId,
    string invocationId
) : IAsyncDisposable
{
    private readonly string _runId = runId.ToString("N");
    private McpClient? _client;

    public async Task<IReadOnlyList<AITool>> ListToolsAsync(
        IReadOnlyList<string> enabledToolNames,
        CancellationToken cancellationToken
    )
    {
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = tandemExePath,
                Arguments = ["mcp", "lifecycle"],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["TANDEM_HOME"] = tandemHome,
                    ["TANDEM_RUN_ID"] = _runId,
                    ["TANDEM_BLOCK_ID"] = blockId,
                    ["TANDEM_INVOCATION_ID"] = invocationId,
                    ["TANDEM_MCP_DIAG"] = "1",
                },
            }
        );

        _client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        var allTools = await _client.ListToolsAsync(cancellationToken: cancellationToken);
        return allTools.Where(t => enabledToolNames.Contains(t.Name)).Cast<AITool>().ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }
}
