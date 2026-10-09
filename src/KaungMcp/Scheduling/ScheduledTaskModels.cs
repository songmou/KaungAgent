using System.Text.Json;

namespace KaungMcp.Scheduling;

public sealed class ScheduledTask
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string ExecutionType { get; set; } = "internal_handler";
    public string Target { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string ScheduleType { get; set; } = "interval";
    public int? IntervalMinutes { get; set; }
    public string? CronExpression { get; set; }
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    public bool IsEnabled { get; set; }
    public bool IsArchived { get; set; }
    public string? RiskSnapshot { get; set; }
    public bool RiskConfirmed { get; set; }
    public long? NextRunTicks { get; set; }
    public string? LastRunStatus { get; set; }
    public long CreatedTicks { get; set; }
    public long UpdatedTicks { get; set; }
}

public sealed class ScheduledTaskRun
{
    public long Id { get; set; }
    public Guid TaskId { get; set; }
    public string Trigger { get; set; } = "scheduled";
    public long? PlannedTicks { get; set; }
    public long StartedTicks { get; set; }
    public long? CompletedTicks { get; set; }
    public string Status { get; set; } = "running";
    public string? Error { get; set; }
    public string? ResultSummary { get; set; }
    public long? InvocationLogId { get; set; }
}

public sealed record ScheduledTaskChange(
    string Name, string? Description, string ExecutionType, string Target, JsonElement Arguments,
    string ScheduleType, int? IntervalMinutes, string? CronExpression, string? TimeZoneId,
    bool IsEnabled, bool ConfirmRisk = false);

public sealed record ScheduleValidationRequest(string ScheduleType, int? IntervalMinutes,
    string? CronExpression, string? TimeZoneId);

public sealed record ScheduleValidationResponse(DateTimeOffset[] NextOccurrencesUtc);

public sealed record ScheduledTaskView(Guid Id, string Name, string? Description, string ExecutionType,
    string Target, JsonElement Arguments, string ScheduleType, int? IntervalMinutes,
    string? CronExpression, string TimeZoneId, bool IsEnabled, bool IsArchived,
    bool RiskConfirmed, string? RiskSnapshot, DateTimeOffset? NextRunAtUtc, string? LastRunStatus,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

public sealed record ScheduledRunView(long Id, Guid TaskId, string Trigger, DateTimeOffset? PlannedAtUtc,
    DateTimeOffset StartedAtUtc, DateTimeOffset? CompletedAtUtc, long? DurationMilliseconds,
    string Status, string? Error, string? ResultSummary, long? InvocationLogId);

public sealed record ScheduledTaskPage(ScheduledTaskView[] Items, int Page, int PageSize,
    int TotalCount, int EnabledCount, int RunningCount, int RecentFailedCount);

public sealed record ScheduledRunPage(ScheduledRunView[] Items, int Page, int PageSize, int TotalCount);

public sealed record SchedulerCatalog(object[] InternalHandlers, object[] Sources, object[] Tools, string[] TimeZones);

public sealed record SchedulerRunOutcome(string Status, string? Summary, string? Error = null,
    long? InvocationLogId = null);
