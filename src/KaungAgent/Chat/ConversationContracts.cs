using KaungAgent.Data.Entities;

namespace KaungAgent.Chat;

public sealed record ConversationToolSelection(Guid ServerId, IReadOnlyList<string>? Tools);
public sealed record ConversationSummary(Guid Id, string Title, Guid ModelProfileId, DateTimeOffset UpdatedAtUtc, int MessageCount);
public sealed record ConversationDetail(
    Guid Id,
    string Title,
    Guid ModelProfileId,
    IReadOnlyList<ConversationToolSelection> ToolSelections,
    IReadOnlyList<ChatMessageResponse> Messages,
    DateTimeOffset UpdatedAtUtc);
public sealed record ChatMessageResponse(
    long Id, string Role, string? Content, string? ToolName, string? Status, string? Error,
    long? InputTokens, long? OutputTokens, long? TotalTokens, long DurationMilliseconds, DateTimeOffset CreatedAtUtc,
    IReadOnlyList<ToolExecutionResponse> ToolExecutions);
public sealed record ToolExecutionResponse(
    long Id, Guid McpServerId, string ToolCallId, string ToolName, string ArgumentsJson,
    string? ResultJson, bool Succeeded, string? Error, long DurationMilliseconds);
public sealed record ChatStreamEvent(string Type, string? Text = null, string? ToolName = null, bool? Succeeded = null, string? Error = null);
