namespace KaungAgent.Models;

public sealed record ModelProfileResponse(
    Guid Id,
    string Name,
    string BaseUrl,
    string ModelId,
    bool HasApiKey,
    IReadOnlyList<string> HeaderNames,
    double Temperature,
    int MaxOutputTokens,
    int TimeoutSeconds,
    bool IsEnabled,
    bool IsDefault,
    DateTimeOffset UpdatedAtUtc);

public sealed class SaveModelProfileRequest
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string ModelId { get; set; } = "";
    public string? ApiKey { get; set; }
    public string? HeadersJson { get; set; }
    public double Temperature { get; set; } = 0.7;
    public int MaxOutputTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 120;
    public bool IsEnabled { get; set; } = true;
    public bool IsDefault { get; set; }
}

public sealed record ModelConnectionTestResult(bool Succeeded, string Message, long DurationMilliseconds);
