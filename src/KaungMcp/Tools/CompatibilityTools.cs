using System.ComponentModel;
using KaungMcp.Security;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace KaungMcp.Tools;

[McpServerToolType]
[McpToolCategory("diagnostics", "基础与诊断", 10, "连接检查与基础兼容工具")]
[Authorize(Policy = SecurityConstants.McpClientPolicy)]
public sealed class CompatibilityTools
{
    [McpServerTool(Name = "kaung_ping", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Checks that the KaungMcp server is available and returns its current UTC time.")]
    public PingResult Ping() => new("ok", DateTimeOffset.UtcNow);

    [McpServerTool(Name = "kaung_echo", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("Returns the supplied message unchanged. Use this tool to verify MCP connectivity.")]
    public EchoResult Echo(
        [Description("Text to return. The maximum length is 4,000 characters.")] string message)
    {
        if (message is null)
        {
            throw new McpException("The message is required.");
        }

        if (message.Length > 4_000)
        {
            throw new McpException("The message cannot exceed 4,000 characters.");
        }

        return new EchoResult(message);
    }
}

public sealed record PingResult(string Status, DateTimeOffset ServerTimeUtc);

public sealed record EchoResult(string Message);
