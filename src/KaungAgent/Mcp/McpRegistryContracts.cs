using System.Text.Json;

namespace KaungAgent.Mcp;

public sealed record McpServerResponse(
    Guid Id,
    string Name,
    string? Description,
    string Endpoint,
    IReadOnlyList<string> HeaderNames,
    int TimeoutSeconds,
    bool IsEnabled,
    bool IsManaged,
    string? LastStatus,
    string? LastError,
    DateTimeOffset? LastCheckedAtUtc,
    DateTimeOffset? LastSyncedAtUtc,
    McpCapabilitySnapshot? Snapshot);

public sealed class SaveMcpServerRequest
{
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string Endpoint { get; set; } = "";
    public string? HeadersJson { get; set; }
    public int TimeoutSeconds { get; set; } = 30;
    public bool IsEnabled { get; set; } = true;
}

public sealed record McpCapabilityItem(
    string Key,
    string Name,
    string? Title,
    string? Description,
    string? Uri,
    string? MimeType,
    JsonElement Definition);

public sealed record McpCapabilitySnapshot(
    JsonElement ServerInfo,
    JsonElement ServerCapabilities,
    string? Instructions,
    IReadOnlyList<McpCapabilityItem> Tools,
    IReadOnlyList<McpCapabilityItem> Prompts,
    IReadOnlyList<McpCapabilityItem> Resources,
    IReadOnlyList<McpCapabilityItem> ResourceTemplates,
    DateTimeOffset SyncedAtUtc);

public sealed record McpOperationResult(bool Succeeded, JsonElement Result, long DurationMilliseconds, string? Error = null);
