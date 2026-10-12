namespace KaungMcp.Data.Entities;

public sealed class McpInvocationLog
{
    public long Id { get; set; }

    public Guid? ClientApplicationId { get; set; }

    public McpClientApplication? ClientApplication { get; set; }

    public string? ToolName { get; set; }

    public required string RequestMethod { get; set; }

    public required string Source { get; set; } = InvocationLogs.InvocationSources.McpClient;

    public string? TargetName { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; }

    public long DurationMilliseconds { get; set; }

    public bool Succeeded { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorMessage { get; set; }

    public string? OwnerId { get; set; }

    public string? OwnerKind { get; set; }

    public string? CorrelationId { get; set; }

    public string? TraceId { get; set; }

    public string? RequestParametersJson { get; set; }

    public string? ResponseBody { get; set; }

    public long? InputTokens { get; set; }

    public long? OutputTokens { get; set; }

    public long? TotalTokens { get; set; }

    public long? BillableUnits { get; set; }

    public long? CostMicros { get; set; }
}
