using System.ComponentModel;
using KaungMcp.Security;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Server;

namespace KaungMcp.Prompts;

[McpServerPromptType]
[Authorize(Policy = SecurityConstants.McpClientPolicy)]
public sealed class DiagnosticPrompts
{
    [McpServerPrompt(Name = "kaung_connection_diagnostic", Title = "Kaung MCP connection diagnostic")]
    [Description("Creates a prompt for checking the KaungMcp connection and summarizing its status.")]
    public string ConnectionDiagnostic() =>
        "Call the kaung_ping tool. Confirm whether KaungMcp is reachable, report the returned server UTC time, " +
        "and clearly explain any error if the call fails.";
}
