using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KaungMcp.Plugin;

public sealed record PluginManifest(string Id, string Name, string Version, int ContractVersion, string EntryAssembly, string EntryType);
public interface IKaungMcpPlugin
{
    void Register(PluginRegistrationContext context);
    Task InitializeAsync(PluginInitializationContext context, CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
public sealed class PluginRegistrationContext(IServiceCollection services, IConfiguration configuration, string dataDirectory)
{
    public IServiceCollection Services { get; } = services;
    public IConfiguration Configuration { get; } = configuration;
    public string DataDirectory { get; } = dataDirectory;
    public List<Assembly> CapabilityAssemblies { get; } = [];
    public List<PluginToolPolicy> Tools { get; } = [];
    public List<PluginTaskDefinition> Tasks { get; } = [];
    public List<DefaultTaskTemplate> DefaultTasks { get; } = [];
    public List<IPluginConfigurationProvider> ConfigurationProviders { get; } = [];
    public List<Type> HealthChecks { get; } = [];
}
public sealed record PluginInitializationContext(IServiceProvider Services, string DataDirectory);
public sealed record PluginToolPolicy(string Name, bool Schedulable = false, bool ClientVisible = true, bool SanitizeErrors = false, Type? ScheduleValidatorType = null);
public sealed record PluginTaskDefinition(string Id, string Name, Type HandlerType, JsonElement ParameterSchema,
    int Version = 1, int DefaultTimeoutSeconds = 300, bool Idempotent = false, int MaxTimeoutSeconds = 86400);
public sealed record DefaultTaskTemplate(string Id, string Name, string HandlerId, JsonElement Arguments,
    int IntervalMinutes, bool IsEnabled = true, string TimeZoneId = "Asia/Shanghai", string ScheduleType = "interval", string? CronExpression = null);
public sealed record PluginScheduleInfo(Guid Id, string PluginId, string TargetType, string TargetId,
    JsonElement Arguments, string ScheduleType, int? IntervalMinutes, string? CronExpression,
    string TimeZoneId, bool IsEnabled, DateTimeOffset? NextRunAtUtc, string? BlockedReason);
public sealed record TaskValidationContext(Guid? PlanId, string ScheduleType, int? IntervalMinutes,
    string? CronExpression, string TimeZoneId, bool IsEnabled, IReadOnlyList<PluginScheduleInfo> Plans);
public interface IPluginTaskValidator
{
    Task ValidateAsync(JsonElement arguments, TaskValidationContext context, CancellationToken cancellationToken);
    IReadOnlyList<string> GetConcurrencyKeys(JsonElement arguments) => [];
}
public interface IPluginTaskHandler : IPluginTaskValidator
{
    Task<PluginTaskResult> ExecuteAsync(JsonElement arguments, PluginTaskExecutionContext context, CancellationToken cancellationToken);
}
public sealed record PluginTaskExecutionContext(Guid RunId, string CorrelationId, string Trigger, int Attempt);
public sealed record ArtifactReference(string Id, string MimeType, string? Name = null);
public sealed record PluginTaskResult(bool Succeeded, string? Summary = null, JsonElement? Result = null,
    ArtifactReference? Artifact = null, string? Error = null);
public interface IPluginTaskService
{
    Task<IReadOnlyList<PluginScheduleInfo>> GetPlansAsync(string pluginId, CancellationToken cancellationToken);
    Task<Guid> SubmitAsync(Guid planId, string trigger, CancellationToken cancellationToken);
    Task<PluginRunInfo?> GetRunAsync(Guid runId, CancellationToken cancellationToken);
}
public sealed record PluginRunInfo(Guid Id, string Status, string? Summary, string? Error);
public interface IPluginConfigurationProvider
{
    IReadOnlyList<PluginSettingDefinition> Settings { get; }
    void Validate(IReadOnlyDictionary<string, string> values);
}
public sealed record PluginSettingDefinition(string Key, string Name, string DefaultValue, bool Runtime = false)
{
    public string GroupId { get; init; } = "general";
    public string GroupName { get; init; } = "常规配置";
    public string? Description { get; init; }
    public string Type { get; init; } = "text";
    public string? Unit { get; init; }
    public decimal? Minimum { get; init; }
    public decimal? Maximum { get; init; }
    public decimal? Step { get; init; }
    public IReadOnlyList<PluginSettingOption>? Options { get; init; }
    public string? ReadOnlyReason { get; init; }
}
public sealed record PluginSettingOption(string Value, string Label);
public interface IPluginHealthCheck
{
    Task<PluginHealth> CheckAsync(CancellationToken cancellationToken);
}
public sealed record PluginHealth(string Status, string? Detail = null);
