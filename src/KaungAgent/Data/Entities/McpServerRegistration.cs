namespace KaungAgent.Data.Entities;

public sealed class McpServerRegistration
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Endpoint { get; set; }
    public string? ProtectedHeadersJson { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public bool IsEnabled { get; set; } = true;
    public bool IsManaged { get; set; }
    public string? ManagedKey { get; set; }
    public string? LastStatus { get; set; }
    public string? LastError { get; set; }
    public string? CapabilitySnapshotJson { get; set; }
    public DateTimeOffset? LastCheckedAtUtc { get; set; }
    public DateTimeOffset? LastSyncedAtUtc { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
