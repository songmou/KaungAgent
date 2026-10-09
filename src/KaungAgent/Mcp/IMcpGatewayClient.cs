namespace KaungAgent.Mcp;

public interface IMcpGatewayClient
{
    Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken);

    Task<InvokeToolResponse> InvokeToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);

    Task PingAsync(CancellationToken cancellationToken);
}
