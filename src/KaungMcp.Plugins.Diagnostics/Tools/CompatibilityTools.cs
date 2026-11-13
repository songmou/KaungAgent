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
    [McpServerTool(Name = "kaung_ping", Title = "连接检查", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("检查 KaungMcp 服务是否可用，并返回当前 UTC 时间。")]
    public PingResult Ping() => new("ok", DateTimeOffset.UtcNow);

    [McpServerTool(Name = "kaung_echo", Title = "消息回显", ReadOnly = true, Idempotent = true, UseStructuredContent = true)]
    [Description("原样返回传入的消息，用于验证 MCP 连接是否正常。")]
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
