namespace KaungAgent.Data.Entities;

public sealed class ToolExecution
{
    public long Id { get; set; }
    public long ChatMessageId { get; set; }
    public ChatMessage? ChatMessage { get; set; }
    public Guid McpServerId { get; set; }
    public required string ToolCallId { get; set; }
    public required string ToolName { get; set; }
    public required string ArgumentsJson { get; set; }
    public string? ResultJson { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public long DurationMilliseconds { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
