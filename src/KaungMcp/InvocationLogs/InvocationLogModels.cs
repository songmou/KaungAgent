namespace KaungMcp.InvocationLogs;

public sealed record InvocationLogResponse(
    long Id,
    Guid? ClientApplicationId,
    string ClientName,
    string Source,
    string? ToolName,
    string RequestMethod,
    string? TargetName,
    DateTimeOffset StartedAtUtc,
    long DurationMilliseconds,
    bool Succeeded,
    string? ErrorCode,
    string? ErrorMessage,
    string? TraceId,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? BillableUnits,
    long? CostMicros,
    string? CorrelationId = null)
{
    public string? OwnerId { get; init; }
    public string? OwnerKind { get; init; }
    public string? OwnerName { get; init; }
    public string? TargetDisplayName { get; init; }
    public Guid? TaskRunId { get; init; }
}

public static class InvocationSources
{
    public const string McpClient = "mcp_client";
    public const string AdminTest = "admin_test";
    public const string Scheduler = "scheduler";

    public static bool IsValid(string value) => value is McpClient or AdminTest or Scheduler;
}

public sealed record InvocationLogDetailResponse(
    InvocationLogResponse Log,
    string? RequestParametersJson,
    string? ResponseBody);
