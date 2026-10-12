using System.ComponentModel;
using System.Text.Json;
using KaungMcp.Security;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace KaungMcp.Resources;

[McpServerResourceType]
[Authorize(Policy = SecurityConstants.McpClientPolicy)]
public sealed class ServerStatusResources
{
    [McpServerResource(
        UriTemplate = "kaung://server/status",
        Name = "kaung_server_status",
        Title = "Kaung MCP server status",
        MimeType = "application/json")]
    [Description("Returns the current KaungMcp availability status and server UTC time.")]
    public string GetServerStatus() => JsonSerializer.Serialize(new
    {
        status = "ok",
        serverTimeUtc = DateTimeOffset.UtcNow
    });
}
