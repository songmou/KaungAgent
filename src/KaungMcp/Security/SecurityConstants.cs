namespace KaungMcp.Security;

public static class SecurityConstants
{
    public const string McpClientScheme = "KaungMcpClient";
    public const string AdminScheme = "KaungMcpAdmin";
    public const string McpClientPolicy = "McpClient";
    public const string AdminPolicy = "McpAdmin";
    public const string ClientIdClaim = "kaung_client_id";
    public const string AdminHeader = "X-Kaung-Admin-Key";
}
