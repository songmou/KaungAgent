using System.Text.Json;

namespace KaungAgent.Mcp;

public sealed record McpToolDescriptor(
    string Name,
    string? Title,
    string? Description,
    JsonElement InputSchema,
    JsonElement? OutputSchema);

public sealed record InvokeToolRequest(JsonElement Arguments);

public sealed record InvokeToolResponse(
    bool IsError,
    JsonElement Content,
    JsonElement? StructuredContent);
