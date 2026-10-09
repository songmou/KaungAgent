namespace KaungAgent.Configuration;

public sealed class McpGatewayOptions
{
    public const string SectionName = "Mcp";

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 30;
}
