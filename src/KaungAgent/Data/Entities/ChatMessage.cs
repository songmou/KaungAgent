namespace KaungAgent.Data.Entities;

public sealed class ChatMessage
{
    public long Id { get; set; }
    public Guid ConversationId { get; set; }
    public Conversation? Conversation { get; set; }
    public required string Role { get; set; }
    public string? Content { get; set; }
    public string? ToolCallId { get; set; }
    public string? ToolName { get; set; }
    public string? ToolCallsJson { get; set; }
    public string? Status { get; set; }
    public string? Error { get; set; }
    public long? SummarizesThroughMessageId { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? TotalTokens { get; set; }
    public long DurationMilliseconds { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public ICollection<ToolExecution> ToolExecutions { get; } = [];
}
