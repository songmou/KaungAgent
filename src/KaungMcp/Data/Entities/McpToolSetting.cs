namespace KaungMcp.Data.Entities;

public sealed class McpToolSetting
{
    public required string ToolName { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
