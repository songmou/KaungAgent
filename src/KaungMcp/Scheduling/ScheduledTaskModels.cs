using System.Text.Json;
using KaungMcp.Plugin;
namespace KaungMcp.Scheduling;

public sealed class ScheduledTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string PluginId { get; set; } = "";
    public string TargetType { get; set; } = "handler";
    public string TargetId { get; set; } = "";
    public int DefinitionVersion { get; set; } = 1;
    public string? TemplateId { get; set; }
    public string ArgumentsJson { get; set; } = "{}";
    public string ScheduleType { get; set; } = "interval";
    public int? IntervalMinutes { get; set; }
    public string? CronExpression { get; set; }
    public string TimeZoneId { get; set; } = "Asia/Shanghai";
    public bool IsEnabled { get; set; }
    public bool IsDeleted { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; }
    public string? RiskSnapshot { get; set; }
    public bool RiskConfirmed { get; set; }
    public long? NextRunTicks { get; set; }
    public string? BlockedReason { get; set; }
    public string? LastRunStatus { get; set; }
    public string? MisfireSummary { get; set; }
    public long CreatedTicks { get; set; }
    public long UpdatedTicks { get; set; }
}
public sealed class TaskRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TaskId { get; set; }
    public string PluginId { get; set; } = "";
    public string PluginVersion { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string TargetId { get; set; } = "";
    public int DefinitionVersion { get; set; }
    public string ArgumentsJson { get; set; } = "{}";
    public string Trigger { get; set; } = "scheduled";
    public long? PlannedTicks { get; set; }
    public long CreatedTicks { get; set; }
    public long? StartedTicks { get; set; }
    public long? CompletedTicks { get; set; }
    public long? RetryAtTicks { get; set; }
    public int Attempt { get; set; }
    public int TimeoutSeconds { get; set; } = 300;
    public int MaxRetries { get; set; }
    public string? RiskSnapshot { get; set; }
    public bool RiskConfirmed { get; set; }
    public bool CancelRequested { get; set; }
    public string CorrelationId { get; set; } = Guid.NewGuid().ToString("N");
    public string Status { get; set; } = "queued";
    public string? Summary { get; set; }
    public string? ResultJson { get; set; }
    public string? ArtifactJson { get; set; }
    public string? Error { get; set; }
    public long? InvocationLogId { get; set; }
}
public sealed record ScheduledTaskChange(string Name, string PluginId, string TargetType, string TargetId,
    JsonElement Arguments, string ScheduleType = "interval", int? IntervalMinutes = 15,
    string? CronExpression = null, string TimeZoneId = "Asia/Shanghai", bool IsEnabled = true,
    int? TimeoutSeconds = null, int MaxRetries = 0, bool ConfirmRisk = false)
{
    public string? OwnerId { get; init; }
    public ScheduledTaskChange NormalizeOwner()
    {
        if (!string.IsNullOrWhiteSpace(OwnerId) && !string.IsNullOrWhiteSpace(PluginId) && OwnerId != PluginId) throw new ArgumentException("ownerId 与 pluginId 不一致。");
        var id = OwnerId ?? PluginId;
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("必须指定所属能力或插件。");
        return this with { PluginId = id };
    }
}
public sealed record ScheduleValidationRequest(string ScheduleType, int? IntervalMinutes, string? CronExpression, string TimeZoneId);
public sealed record ScheduleValidationResponse(DateTimeOffset[] NextOccurrencesUtc);
public sealed record ScheduledTaskView(Guid Id, string Name, string PluginId, string TargetType, string TargetId,
    JsonElement Arguments, int DefinitionVersion, string? TemplateId, string ScheduleType, int? IntervalMinutes,
    string? CronExpression, string TimeZoneId, bool IsEnabled, bool IsDeleted, int TimeoutSeconds, int MaxRetries,
    bool RiskConfirmed, DateTimeOffset? NextRunAtUtc, string? BlockedReason, string? LastRunStatus, string? MisfireSummary)
{
    public string OwnerId => PluginId;
    public string OwnerKind { get; init; } = "plugin";
    public string? OwnerName { get; init; }
    public string? TargetName { get; init; }
}
public sealed record TaskRunView(Guid Id, Guid TaskId, string PluginId, string PluginVersion, string TargetType,
    string TargetId, int DefinitionVersion, JsonElement Arguments, string Trigger, DateTimeOffset? PlannedAtUtc,
    DateTimeOffset CreatedAtUtc, DateTimeOffset? StartedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset? RetryAtUtc,
    string Status, int Attempt, bool CancelRequested, string CorrelationId, string? Summary, JsonElement? Result,
    ArtifactReference? Artifact, string? Error, long? InvocationLogId)
{
    public string OwnerId => PluginId;
    public string OwnerKind { get; init; } = "plugin";
    public string? OwnerName { get; init; }
    public string? TargetName { get; init; }
    public string? TaskName { get; init; }
    public long? DurationMilliseconds => StartedAtUtc is { } start && CompletedAtUtc is { } end ? (long)(end - start).TotalMilliseconds : null;
}
public sealed record SchedulerPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);
