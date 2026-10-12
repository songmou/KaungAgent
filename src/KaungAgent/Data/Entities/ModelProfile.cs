namespace KaungAgent.Data.Entities;

public sealed class ModelProfile
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string BaseUrl { get; set; }
    public required string ModelId { get; set; }
    public string? ProtectedApiKey { get; set; }
    public string? ProtectedHeadersJson { get; set; }
    public double Temperature { get; set; } = 0.7;
    public int MaxOutputTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 120;
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
