using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace KaungMcp.Tools;

public sealed record McpToolCatalogResponse(
    IReadOnlyList<McpToolCategoryResponse> Categories,
    int TotalCount,
    int EnabledCount,
    int DisabledCount);

public sealed record McpToolCategoryResponse(
    string Id,
    string DisplayName,
    string? Description,
    int Order,
    IReadOnlyList<McpToolResponse> Tools);

public sealed record McpToolResponse(
    string Name,
    string? Title,
    string? Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema,
    bool? ReadOnly,
    bool? Destructive,
    bool? Idempotent,
    bool? OpenWorld,
    bool IsEnabled,
    int ParameterCount,
    bool RequiresRiskConfirmation,
    string? PluginId,
    bool Schedulable,
    bool IsAvailable)
{
    public string? OwnerId { get; init; }
    public string? OwnerKind { get; init; }
    public string? OwnerName { get; init; }
}

public sealed record SetMcpToolEnabledRequest(bool IsEnabled);

public sealed record TestMcpToolRequest(JsonElement Arguments, bool ConfirmRisk = false);

public sealed record TestMcpToolResponse(
    bool Succeeded,
    long DurationMilliseconds,
    CallToolResult Result,
    string? Error,
    long? LogId,
    bool IsEnabled);
