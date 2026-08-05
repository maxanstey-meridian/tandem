using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Tandem.Infrastructure.Lifecycle;

public sealed class LifecycleMcpClient : IAsyncDisposable
{
    private readonly string _tandemHome;
    private readonly string _tandemExePath;
    private readonly string _runId;
    private readonly string _blockId;
    private readonly string _invocationId;
    private McpClient? _client;

    public LifecycleMcpClient(
        string tandemHome,
        string tandemExePath,
        Guid runId,
        string blockId,
        string invocationId
    )
    {
        _tandemHome = tandemHome;
        _tandemExePath = tandemExePath;
        _runId = runId.ToString("N");
        _blockId = blockId;
        _invocationId = invocationId;
    }

    public async Task<IReadOnlyList<AITool>> ListToolsAsync(
        IReadOnlyList<string> enabledToolNames,
        CancellationToken cancellationToken
    )
    {
        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = _tandemExePath,
                Arguments = ["mcp", "lifecycle"],
                EnvironmentVariables = new Dictionary<string, string?>
                {
                    ["TANDEM_HOME"] = _tandemHome,
                    ["TANDEM_RUN_ID"] = _runId,
                    ["TANDEM_BLOCK_ID"] = _blockId,
                    ["TANDEM_INVOCATION_ID"] = _invocationId,
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
