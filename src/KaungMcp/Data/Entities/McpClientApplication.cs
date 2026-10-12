namespace KaungMcp.Data.Entities;

public sealed class McpClientApplication
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public required string KeyId { get; set; }

    public required byte[] KeyHash { get; set; }

    public bool IsEnabled { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    public DateTimeOffset? LastUsedAtUtc { get; set; }

    public DateTimeOffset? DeletedAtUtc { get; set; }

    public ICollection<McpInvocationLog> InvocationLogs { get; } = [];
}
