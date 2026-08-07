using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Tandem.Infrastructure.Lifecycle;

public sealed class LifecycleMcpClient(
    string tandemHome,
    string tandemExePath,
    Guid runId,
    string blockId,
    string invocationId,
    string actionSetIdentity
) : IAsyncDisposable
{
    private readonly string _runId = runId.ToString("N");
    private McpClient? _client;

    public async Task<IReadOnlyList<AITool>> ListToolsAsync(
        IReadOnlyList<string> enabledToolNames,
        CancellationToken cancellationToken
    )
    {
        if (_client is not null)
        {
            throw new InvalidOperationException("Lifecycle MCP client is already started.");
        }

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Command = tandemExePath,
                Arguments = ["mcp", actionSetIdentity],
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
        var requested = enabledToolNames.ToHashSet(StringComparer.Ordinal);
        var tools = allTools.Where(t => requested.Contains(t.Name)).ToList();
        var missing = requested.Except(tools.Select(tool => tool.Name)).Order().ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Lifecycle action set '{actionSetIdentity}' is missing requested tool(s): {string.Join(", ", missing)}."
            );
        }

        return tools.Cast<AITool>().ToList();
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            var client = _client;
            _client = null;
            await client.DisposeAsync();
        }
    }
}
