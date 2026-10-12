namespace KaungAgent.Data.Entities;

public sealed class Conversation
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public Guid ModelProfileId { get; set; }
    public ModelProfile? ModelProfile { get; set; }
    public string ToolSelectionJson { get; set; } = "[]";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public ICollection<ChatMessage> Messages { get; } = [];
}
