using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Cronos;
using KaungMcp.Data;
using KaungMcp.InvocationLogs;
using KaungMcp.Plugin;
using KaungMcp.Plugins;
using KaungMcp.Capabilities;
using KaungMcp.Tools;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.Scheduling;

public sealed class SchedulerService(
    IDbContextFactory<KaungMcpDbContext> database,
    IServiceProvider services,
    PluginRuntime plugins,
    TimeProvider clock,
    IConfiguration configuration,
    ILogger<SchedulerService> logger) : IPluginTaskService
{
    private CapabilityRegistry Capabilities => services.GetRequiredService<CapabilityRegistry>();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly SemaphoreSlim dispatch = new(1, 1);
    private readonly ConcurrentDictionary<Guid, ActiveRun> active = new();
    private readonly HashSet<string> resourceLocks = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource forceStop = new();
    private bool initialized;
    private volatile bool stopping;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private sealed class ActiveRun(string pluginId, IReadOnlyList<string> keys, CancellationTokenSource cancellation)
    {
        public string PluginId { get; } = pluginId;
        public IReadOnlyList<string> Keys { get; } = keys;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; set; } = Task.CompletedTask;
    }
    private sealed record TargetInfo(int Version, int Timeout, int MaxTimeout, bool Idempotent, string? Risk, bool Risky, IReadOnlyList<string> Keys);
    private static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);
    private static DateTimeOffset? At(long? ticks) => ticks is { } value ? At(value) : null;
    private static JsonElement Parse(string value) => JsonSerializer.Deserialize<JsonElement>(value);
    public static DateTimeOffset Next(ScheduleValidationRequest request, DateTimeOffset from)
    {
        TimeZoneInfo zone;
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(request.TimeZoneId); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new ArgumentException("时区无效。", e); }
        if (request.ScheduleType == "interval")
        {
            if (request.IntervalMinutes is null or < 1 or > 10080) throw new ArgumentException("周期必须为 1..10080 分钟。");
            return from.AddMinutes(request.IntervalMinutes.Value);
        }
        if (request.ScheduleType != "cron" || string.IsNullOrWhiteSpace(request.CronExpression)) throw new ArgumentException("计划类型无效。");
        try
        {
            var next = CronExpression.Parse(request.CronExpression, CronFormat.Standard).GetNextOccurrence(from.UtcDateTime, zone)
                ?? throw new ArgumentException("Cron 没有未来执行时间。");
            return new(next, TimeSpan.Zero);
        }
        catch (CronFormatException e) { throw new ArgumentException("五段 Cron 表达式无效。", e); }
    }
    public static ScheduleValidationResponse ValidateSchedule(ScheduleValidationRequest request, DateTimeOffset now)
    {
        var result = new DateTimeOffset[5];
        for (var i = 0; i < result.Length; i++) result[i] = now = Next(request, now);
        return new(result);
    }
    private static ScheduleValidationRequest Schedule(ScheduledTask plan) => new(plan.ScheduleType, plan.IntervalMinutes, plan.CronExpression, plan.TimeZoneId);
    private static ScheduleValidationRequest Schedule(ScheduledTaskChange change) => new(change.ScheduleType, change.IntervalMinutes, change.CronExpression, change.TimeZoneId);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            if (initialized) return;
            await using var db = await database.CreateDbContextAsync(ct);
            var now = clock.GetUtcNow();
            foreach (var run in await db.TaskRuns.Where(r => r.Status == "running").ToListAsync(ct))
            {
                run.Status = "interrupted"; run.CompletedTicks = now.UtcTicks; run.Error = "服务在执行期间重启，未自动重放。";
                var owner = await db.ScheduledTasks.FindAsync([run.TaskId], ct);
                if (owner is not null) owner.LastRunStatus = run.Status;
            }
            foreach (var plugin in plugins.Plugins.Where(p => plugins.Available(p.Manifest.Id)))
                foreach (var template in plugin.Registration!.DefaultTasks)
                {
                    if (await db.ScheduledTasks.AnyAsync(p => p.PluginId == plugin.Manifest.Id && p.TemplateId == template.Id, ct)) continue;
                    var definition = plugins.TaskDefinition(plugin.Manifest.Id, template.HandlerId)!;
                    var change = new ScheduledTaskChange(template.Name, plugin.Manifest.Id, "handler", template.HandlerId,
                        template.Arguments, ScheduleType: template.ScheduleType, IntervalMinutes: template.IntervalMinutes,
                        CronExpression: template.CronExpression, TimeZoneId: template.TimeZoneId, IsEnabled: template.IsEnabled);
                    TargetInfo info;
                    string? blocked = null;
                    try { info = await ValidateTargetAsync(change, null, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error)
                    {
                        blocked = error.GetBaseException().Message;
                        info = new(definition.Version, definition.DefaultTimeoutSeconds, definition.MaxTimeoutSeconds, definition.Idempotent, null, false, []);
                    }
                    var plan = new ScheduledTask { TemplateId = template.Id, CreatedTicks = now.UtcTicks };
                    Apply(plan, change, info, now);
                    plan.BlockedReason = blocked;
                    if (blocked is not null) plan.NextRunTicks = null;
                    db.ScheduledTasks.Add(plan);
                    // Save each template so subsequent validation sees its source ownership.
                    await db.SaveChangesAsync(ct);
                }
            foreach (var plan in await db.ScheduledTasks.Where(p => !p.IsDeleted).ToListAsync(ct))
            {
                plan.BlockedReason = await BlockReasonAsync(plan, ct);
                if (plan.IsEnabled && plan.BlockedReason is null)
                {
                    if (plan.NextRunTicks is null || plan.NextRunTicks <= now.UtcTicks)
                    {
                        if (plan.NextRunTicks is not null) plan.MisfireSummary = $"服务停机期间错过的执行点已跳过；恢复于 {now:O}。";
                        plan.NextRunTicks = Next(Schedule(plan), now).UtcTicks;
                    }
                }
                else plan.NextRunTicks = null;
            }
            await db.SaveChangesAsync(ct);
            initialized = true;
        }
        finally { writes.Release(); }
    }

    private async Task<TargetInfo> ValidateTargetAsync(ScheduledTaskChange change, Guid? id, CancellationToken ct)
    {
        if (!Capabilities.Available(change.PluginId)) throw new ArgumentException("所属能力未启用、未加载或初始化失败。");
        if (change.Arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数必须为 JSON 对象。");
        await using var scope = services.CreateAsyncScope();
        if (change.TargetType == "handler")
        {
            var definition = plugins.TaskDefinition(change.PluginId, change.TargetId) ?? throw new ArgumentException("任务处理器不存在。");
            ArgumentSchema.Validate(change.Arguments, definition.ParameterSchema);
            var handler = (IPluginTaskHandler)scope.ServiceProvider.GetRequiredService(definition.HandlerType);
            var plans = await GetPlansAsync(change.PluginId, ct);
            await handler.ValidateAsync(change.Arguments, new(id, change.ScheduleType, change.IntervalMinutes, change.CronExpression,
                change.TimeZoneId, change.IsEnabled, plans), ct);
            return new(definition.Version, definition.DefaultTimeoutSeconds, definition.MaxTimeoutSeconds, definition.Idempotent, null, false,
                handler.GetConcurrencyKeys(change.Arguments));
        }
        if (change.TargetType != "tool" || Capabilities.ToolOwner(change.TargetId) != change.PluginId || Capabilities.ToolPolicy(change.TargetId)?.Schedulable != true)
            throw new ArgumentException("工具不存在或未允许调度。");
        var manager = scope.ServiceProvider.GetRequiredService<McpToolManagementService>();
        if (!await manager.IsEnabledAsync(change.TargetId, ct)) throw new ArgumentException("工具已停用或不允许调度。");
        var tool = (await manager.GetCatalogAsync(ct)).Categories.SelectMany(c => c.Tools).Single(t => t.Name == change.TargetId);
        ArgumentSchema.Validate(change.Arguments, tool.InputSchema);
        var risk = JsonSerializer.Serialize(new { tool.ReadOnly, tool.Destructive });
        if (tool.RequiresRiskConfirmation && !change.ConfirmRisk) throw new ArgumentException("此工具需要确认无人值守执行风险。");
        IReadOnlyList<string> keys = [];
        if (Capabilities.ToolPolicy(change.TargetId)?.ScheduleValidatorType is { } validatorType)
        {
            var validator = (IPluginTaskValidator)scope.ServiceProvider.GetRequiredService(validatorType);
            await validator.ValidateAsync(change.Arguments, new(id, change.ScheduleType, change.IntervalMinutes, change.CronExpression,
                change.TimeZoneId, change.IsEnabled, await GetPlansAsync(change.PluginId, ct)), ct);
            keys = validator.GetConcurrencyKeys(change.Arguments);
        }
        return new(1, 300, 86400, tool.Idempotent == true, risk, tool.RequiresRiskConfirmation, keys);
    }
    private static ScheduledTaskChange Change(ScheduledTask plan) => new(plan.Name, plan.PluginId, plan.TargetType, plan.TargetId,
        Parse(plan.ArgumentsJson), plan.ScheduleType, plan.IntervalMinutes, plan.CronExpression, plan.TimeZoneId,
        plan.IsEnabled, plan.TimeoutSeconds, plan.MaxRetries, plan.RiskConfirmed);
    private async Task<string?> BlockReasonAsync(ScheduledTask plan, CancellationToken ct)
    {
        try
        {
            ValidateChange(Change(plan));
            var info = await ValidateTargetAsync(Change(plan), plan.Id, ct);
            if (info.Version != plan.DefinitionVersion) return "处理器定义版本已改变，请编辑计划。";
            if (info.Risk != plan.RiskSnapshot) return "工具风险声明已改变，请重新确认。";
            if (plan.TimeoutSeconds > info.MaxTimeout || plan.MaxRetries > 0 && !info.Idempotent) return "执行策略已不兼容，请编辑计划。";
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) { return error.GetBaseException().Message; }
    }
    private void Apply(ScheduledTask plan, ScheduledTaskChange change, TargetInfo info, DateTimeOffset now)
    {
        plan.Name = change.Name.Trim(); plan.PluginId = change.PluginId; plan.TargetType = change.TargetType; plan.TargetId = change.TargetId;
        plan.ArgumentsJson = change.Arguments.GetRawText(); plan.DefinitionVersion = info.Version;
        plan.ScheduleType = change.ScheduleType; plan.IntervalMinutes = change.ScheduleType == "interval" ? change.IntervalMinutes : null;
        plan.CronExpression = change.ScheduleType == "cron" ? change.CronExpression : null;
        plan.TimeZoneId = change.TimeZoneId; plan.IsEnabled = change.IsEnabled; plan.TimeoutSeconds = change.TimeoutSeconds ?? info.Timeout;
        plan.MaxRetries = change.MaxRetries; plan.RiskSnapshot = info.Risk; plan.RiskConfirmed = change.ConfirmRisk;
        plan.UpdatedTicks = now.UtcTicks; plan.BlockedReason = null;
        plan.NextRunTicks = change.IsEnabled ? Next(Schedule(plan), now).UtcTicks : null;
    }
    private static void ValidateChange(ScheduledTaskChange change)
    {
        if (string.IsNullOrWhiteSpace(change.Name) || change.Name.Length > 120 || string.IsNullOrWhiteSpace(change.PluginId) || change.PluginId.Length > 64 || string.IsNullOrWhiteSpace(change.TargetId) || change.TargetId.Length > 200)
            throw new ArgumentException("任务名称或目标无效。");
        if (change.MaxRetries is < 0 or > 2 || change.TimeoutSeconds is < 1 or > 86400) throw new ArgumentException("超时或重试设置无效。");
        _ = ValidateSchedule(Schedule(change), DateTimeOffset.UtcNow);
    }
    private static void ValidatePolicy(ScheduledTaskChange change, TargetInfo info)
    {
        if (change.MaxRetries > 0 && !info.Idempotent) throw new ArgumentException("只有声明幂等的目标才能重试。");
        if (change.TimeoutSeconds > info.MaxTimeout) throw new ArgumentException("超时超过处理器允许的上限。");
    }
    public async Task<object> PreviewAsync(ScheduledTaskChange change, Guid? planId, CancellationToken ct)
    {
        change = change.NormalizeOwner();
        ValidateChange(change); var info = await ValidateTargetAsync(change, planId, ct); ValidatePolicy(change, info);
        return ValidateSchedule(Schedule(change), clock.GetUtcNow());
    }
    public async Task<ScheduledTaskView> CreateAsync(ScheduledTaskChange change, CancellationToken ct)
    {
        change = change.NormalizeOwner();
        await writes.WaitAsync(ct);
        try
        {
            if (stopping) throw new InvalidOperationException("服务正在停止。");
            ValidateChange(change); var info = await ValidateTargetAsync(change, null, ct); ValidatePolicy(change, info);
            var now = clock.GetUtcNow(); var plan = new ScheduledTask { CreatedTicks = now.UtcTicks };
            Apply(plan, change, info, now);
            await using var db = await database.CreateDbContextAsync(ct); db.ScheduledTasks.Add(plan); await db.SaveChangesAsync(ct); return View(plan);
        }
        finally { writes.Release(); }
    }
    public async Task<ScheduledTaskView?> UpdateAsync(Guid id, ScheduledTaskChange change, CancellationToken ct)
    {
        change = change.NormalizeOwner();
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var plan = await db.ScheduledTasks.SingleOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct); if (plan is null) return null;
            if (plan.PluginId != change.PluginId || plan.TargetType != change.TargetType || plan.TargetId != change.TargetId)
                throw new ArgumentException("计划目标不可变更，请创建新计划。");
            ValidateChange(change); var info = await ValidateTargetAsync(change, id, ct); ValidatePolicy(change, info);
            Apply(plan, change, info, clock.GetUtcNow());
            foreach (var run in await db.TaskRuns.Where(r => r.TaskId == id && r.Status == "queued").ToListAsync(ct))
                CancelQueued(run, "计划已修改。");
            await db.SaveChangesAsync(ct); return View(plan);
        }
        finally { writes.Release(); }
    }
    public async Task<ScheduledTaskView?> SetStateAsync(Guid id, string action, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var plan = await db.ScheduledTasks.SingleOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct); if (plan is null) return null;
            if (action == "delete") { plan.IsDeleted = true; plan.IsEnabled = false; }
            else if (action == "disable") plan.IsEnabled = false;
            else if (action == "enable")
            {
                var change = Change(plan) with { IsEnabled = true };
                var info = await ValidateTargetAsync(change, plan.Id, ct); ValidatePolicy(change, info);
                if (info.Version != plan.DefinitionVersion || info.Risk != plan.RiskSnapshot) throw new ArgumentException("目标声明已改变，请编辑计划。");
                plan.IsEnabled = true;
            }
            else throw new ArgumentException("未知操作。");
            plan.NextRunTicks = plan.IsEnabled ? Next(Schedule(plan), clock.GetUtcNow()).UtcTicks : null;
            plan.UpdatedTicks = clock.GetUtcNow().UtcTicks;
            if (!plan.IsEnabled) foreach (var run in await db.TaskRuns.Where(r => r.TaskId == id && r.Status == "queued" && r.Trigger == "scheduled").ToListAsync(ct)) CancelQueued(run, "计划已停用或删除。");
            if (plan.IsDeleted) foreach (var run in await db.TaskRuns.Where(r => r.TaskId == id && r.Status == "queued").ToListAsync(ct)) CancelQueued(run, "计划已删除。");
            await db.SaveChangesAsync(ct); return View(plan);
        }
        finally { writes.Release(); }
    }
    private void CancelQueued(TaskRun run, string reason)
    {
        run.Status = "cancelled"; run.CancelRequested = true; run.CompletedTicks = clock.GetUtcNow().UtcTicks; run.Error = reason;
    }
    public async Task RefreshPluginAsync(string pluginId, CancellationToken ct, string? toolName = null)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            foreach (var plan in await db.ScheduledTasks.Where(p => p.PluginId == pluginId && !p.IsDeleted && (toolName == null || p.TargetType == "tool" && p.TargetId == toolName)).ToListAsync(ct))
            {
                plan.BlockedReason = await BlockReasonAsync(plan, ct);
                plan.NextRunTicks = plan.IsEnabled && plan.BlockedReason is null ? Next(Schedule(plan), clock.GetUtcNow()).UtcTicks : null;
            }
            foreach (var queued in await db.TaskRuns.Where(r => r.PluginId == pluginId && r.Status == "queued" && (toolName == null || r.TargetId == toolName)).ToListAsync(ct))
                if (!Capabilities.Available(pluginId) || (await db.ScheduledTasks.FindAsync([queued.TaskId], ct))?.BlockedReason is not null) CancelQueued(queued, "所属能力或工具已停用或不可用。");
            await db.SaveChangesAsync(ct);
        }
        finally { writes.Release(); }
    }
    private TaskRun NewRun(ScheduledTask plan, string trigger, long? planned) => new()
    {
        TaskId = plan.Id,
        PluginId = plan.PluginId,
        PluginVersion = Capabilities.Version(plan.PluginId) ?? "unknown",
        TargetType = plan.TargetType,
        TargetId = plan.TargetId,
        DefinitionVersion = plan.DefinitionVersion,
        ArgumentsJson = plan.ArgumentsJson,
        Trigger = trigger,
        PlannedTicks = planned,
        CreatedTicks = clock.GetUtcNow().UtcTicks,
        TimeoutSeconds = plan.TimeoutSeconds,
        MaxRetries = plan.MaxRetries,
        RiskSnapshot = plan.RiskSnapshot,
        RiskConfirmed = plan.RiskConfirmed
    };
    public async Task<Guid> SubmitAsync(Guid planId, string trigger, CancellationToken ct)
    {
        if (trigger is not ("manual" or "plugin")) throw new ArgumentException("触发来源无效。");
        await writes.WaitAsync(ct);
        try
        {
            if (stopping) throw new InvalidOperationException("服务正在停止。");
            await using var db = await database.CreateDbContextAsync(ct);
            var plan = await db.ScheduledTasks.SingleOrDefaultAsync(p => p.Id == planId && !p.IsDeleted, ct) ?? throw new KeyNotFoundException("计划不存在。");
            if (trigger == "plugin" && !plan.IsEnabled) throw new ArgumentException("计划已停用。");
            var reason = await BlockReasonAsync(plan, ct); if (reason is not null) throw new ArgumentException(reason);
            var run = NewRun(plan, trigger, null);
            if (await db.TaskRuns.AnyAsync(r => r.TaskId == planId && (r.Status == "queued" || r.Status == "running"), ct))
            { run.Status = "skipped"; run.CompletedTicks = clock.GetUtcNow().UtcTicks; run.Error = "同一计划已有排队或运行任务。"; }
            db.TaskRuns.Add(run); await db.SaveChangesAsync(ct); return run.Id;
        }
        finally { writes.Release(); }
    }
    public async Task TickAsync(CancellationToken ct)
    {
        if (stopping) return;
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var now = clock.GetUtcNow();
            foreach (var plan in await db.ScheduledTasks.Where(p => p.IsEnabled && !p.IsDeleted).ToListAsync(ct))
            {
                var prior = plan.BlockedReason;
                plan.BlockedReason = await BlockReasonAsync(plan, ct);
                if (plan.BlockedReason is not null) { plan.NextRunTicks = null; continue; }
                if (prior is not null || plan.NextRunTicks is null) { plan.NextRunTicks = Next(Schedule(plan), now).UtcTicks; continue; }
                if (plan.NextRunTicks > now.UtcTicks) continue;
                var planned = plan.NextRunTicks.Value;
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                if (!await db.TaskRuns.AnyAsync(r => r.TaskId == plan.Id && r.PlannedTicks == planned, ct))
                {
                    var run = NewRun(plan, "scheduled", planned);
                    if (await db.TaskRuns.AnyAsync(r => r.TaskId == plan.Id && (r.Status == "queued" || r.Status == "running"), ct))
                    { run.Status = "skipped"; run.CompletedTicks = now.UtcTicks; run.Error = "同一计划已有排队或运行任务。"; }
                    db.TaskRuns.Add(run);
                }
                var next = Next(Schedule(plan), At(planned));
                if (next <= now)
                {
                    plan.MisfireSummary = $"跳过积压执行点；最近扫描于 {now:O}。";
                    if (plan.ScheduleType == "interval")
                    {
                        var interval = TimeSpan.FromMinutes(plan.IntervalMinutes!.Value).Ticks;
                        next = At(planned + ((now.UtcTicks - planned) / interval + 1) * interval);
                    }
                    else next = Next(Schedule(plan), now);
                }
                plan.NextRunTicks = next.UtcTicks;
                await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            }
            await db.SaveChangesAsync(ct);
        }
        finally { writes.Release(); }
        await DispatchAsync(ct);
    }
    public async Task DispatchAsync(CancellationToken ct)
    {
        await dispatch.WaitAsync(ct);
        try
        {
            foreach (var pair in active.Where(p => p.Value.Completion.IsCompleted).ToArray())
            {
                if (active.TryRemove(pair.Key, out var completed))
                { foreach (var key in completed.Keys) resourceLocks.Remove(key); completed.Cancellation.Dispose(); }
            }
            if (stopping) return;
            var global = Math.Clamp(configuration.GetValue("Scheduler:MaxConcurrency", 4), 1, 64);
            var perPlugin = Math.Clamp(configuration.GetValue("Scheduler:PluginConcurrency", 2), 1, 64);
            await using var db = await database.CreateDbContextAsync(ct);
            var now = clock.GetUtcNow().UtcTicks;
            var queue = await db.TaskRuns.AsNoTracking().Where(r => r.Status == "queued" && (r.RetryAtTicks == null || r.RetryAtTicks <= now))
                .OrderBy(r => r.CreatedTicks).Take(200).ToListAsync(ct);
            foreach (var run in queue)
            {
                if (active.Count >= global) break;
                if (active.ContainsKey(run.Id) || active.Values.Count(a => a.PluginId == run.PluginId) >= perPlugin) continue;
                IReadOnlyList<string> keys = [];
                try
                {
                    var plan = await db.ScheduledTasks.AsNoTracking().SingleAsync(p => p.Id == run.TaskId, ct);
                    var change = Change(plan) with { Arguments = Parse(run.ArgumentsJson), ConfirmRisk = run.RiskConfirmed };
                    var info = await ValidateTargetAsync(change, plan.Id, ct); keys = info.Keys;
                    if (resourceLocks.Overlaps(keys)) continue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error) { await FinishAsync(run.Id, "skipped", null, error.GetBaseException().Message, null, null, ct); continue; }
                var cancellation = CancellationTokenSource.CreateLinkedTokenSource(forceStop.Token);
                var holder = new ActiveRun(run.PluginId, keys, cancellation);
                foreach (var key in keys) resourceLocks.Add(key);
                active.TryAdd(run.Id, holder);
                holder.Completion = ExecuteAsync(run.Id, holder);
            }
        }
        finally { dispatch.Release(); }
    }
    private async Task ExecuteAsync(Guid id, ActiveRun holder)
    {
        try
        {
            TaskRun run;
            await writes.WaitAsync();
            try
            {
                await using var db = await database.CreateDbContextAsync();
                run = await db.TaskRuns.SingleAsync(r => r.Id == id);
                if (run.Status != "queued") return;
                var plan = await db.ScheduledTasks.SingleAsync(p => p.Id == run.TaskId);
                var blocked = await BlockReasonAsync(plan, CancellationToken.None);
                if (plan.IsDeleted || run.Trigger == "scheduled" && !plan.IsEnabled || blocked is not null ||
                    run.DefinitionVersion != plan.DefinitionVersion || run.RiskSnapshot != plan.RiskSnapshot ||
                    Capabilities.Version(run.PluginId) != run.PluginVersion)
                {
                    run.Status = "skipped"; run.Error = blocked ?? "计划或插件版本已改变。"; run.CompletedTicks = clock.GetUtcNow().UtcTicks;
                    await db.SaveChangesAsync(); return;
                }
                run.Status = "running"; run.StartedTicks = clock.GetUtcNow().UtcTicks; run.Attempt++;
                await db.SaveChangesAsync();
            }
            finally { writes.Release(); }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(run.TimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(holder.Cancellation.Token, timeout.Token);
            using var activity = new Activity("plugin.task").SetIdFormat(ActivityIdFormat.W3C).Start();
            activity.SetTag("task.correlation_id", run.CorrelationId);
            await using var scope = services.CreateAsyncScope();
            PluginTaskResult result;
            long? logId = null;
            try
            {
                if (!Capabilities.Available(run.PluginId)) throw new InvalidOperationException("插件已停用。");
                if (run.TargetType == "handler")
                {
                    var definition = plugins.TaskDefinition(run.PluginId, run.TargetId)!;
                    var handler = (IPluginTaskHandler)scope.ServiceProvider.GetRequiredService(definition.HandlerType);
                    result = await handler.ExecuteAsync(Parse(run.ArgumentsJson), new(run.Id, run.CorrelationId, run.Trigger, run.Attempt), linked.Token);

                }
                else
                {
                    var response = await scope.ServiceProvider.GetRequiredService<McpToolManagementService>()
                        .TestAsync(run.TargetId, Parse(run.ArgumentsJson), run.RiskConfirmed, linked.Token, InvocationSources.Scheduler, run.CorrelationId);
                    result = new(response!.Succeeded, response.Succeeded ? "工具执行成功。" : "工具执行失败。", JsonSerializer.SerializeToElement(response.Result), Error: response.Error);
                    logId = response.LogId;
                }
                var status = CancellationStatus() ?? (result.Succeeded ? "succeeded" : "failed");
                if (status is "cancelled" or "timed_out" or "interrupted") result = result with { Error = "执行已结束：" + status };
                if (logId is null || status is "cancelled" or "timed_out" or "interrupted") logId = await WriteAttemptLogAsync(run, status, result, scope.ServiceProvider);
                await CompleteAttemptAsync(run, status, result, logId);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                var status = CancellationStatus() ?? "cancelled";
                var failure = new PluginTaskResult(false, Error: "执行已结束：" + status);
                logId = await WriteAttemptLogAsync(run, status, failure, scope.ServiceProvider);
                await CompleteAttemptAsync(run, status, failure, logId);
            }
            catch (Exception error)
            {
                var status = CancellationStatus() ?? "failed";
                var failure = new PluginTaskResult(false, Error: error.GetBaseException().Message);
                logId = await WriteAttemptLogAsync(run, status, failure, scope.ServiceProvider);
                await CompleteAttemptAsync(run, status, failure, logId);
            }
            string? CancellationStatus() => forceStop.IsCancellationRequested ? "interrupted" :
                timeout.IsCancellationRequested ? "timed_out" : holder.Cancellation.IsCancellationRequested ? "cancelled" : null;
        }
        catch (Exception error)
        {
            logger.LogError(error, "Task run {RunId} failed", id);
            try { await FinishAsync(id, "failed", null, error.GetBaseException().Message, null, null, CancellationToken.None); }
            catch (Exception persistenceError) { logger.LogError(persistenceError, "Cannot persist failure of {RunId}", id); }
        }
    }
    private Task<long?> WriteAttemptLogAsync(TaskRun run, string status, PluginTaskResult result, IServiceProvider scope) =>
        scope.GetRequiredService<McpInvocationLogService>().WriteAsync(null,
            run.TargetType == "handler" ? "tasks/execute" : "tools/call", run.PluginId + "/" + run.TargetId,
            At(run.StartedTicks!.Value), (long)(clock.GetUtcNow() - At(run.StartedTicks.Value)).TotalMilliseconds,
            status == "succeeded", status == "succeeded" ? null : status, result.Error, run.ArgumentsJson,
            JsonSerializer.Serialize(new { run.Attempt, status, result }, Json), InvocationSources.Scheduler, run.CorrelationId, run.PluginId, Capabilities.Kind(run.PluginId));
    private async Task CompleteAttemptAsync(TaskRun snapshot, string status, PluginTaskResult result, long? logId)
    {
        await writes.WaitAsync();
        try
        {
            await using var db = await database.CreateDbContextAsync();
            var run = await db.TaskRuns.SingleAsync(r => r.Id == snapshot.Id);
            if (run.CancelRequested && status is "failed" or "succeeded") status = "cancelled";
            run.Summary = result.Summary; run.ResultJson = result.Result?.GetRawText();
            run.ArtifactJson = result.Artifact is null ? null : JsonSerializer.Serialize(result.Artifact, Json);
            run.Error = result.Error; run.InvocationLogId = logId;
            if (status == "failed" && run.Attempt <= run.MaxRetries && Capabilities.Available(run.PluginId))
            {
                run.Status = "queued"; run.RetryAtTicks = clock.GetUtcNow().AddSeconds(run.Attempt == 1 ? 30 : 120).UtcTicks;
            }
            else { run.Status = status; run.CompletedTicks = clock.GetUtcNow().UtcTicks; run.RetryAtTicks = null; }
            var plan = await db.ScheduledTasks.FindAsync(run.TaskId); if (plan is not null) plan.LastRunStatus = run.Status;
            await db.SaveChangesAsync();
        }
        finally { writes.Release(); }
    }
    private async Task FinishAsync(Guid id, string status, string? summary, string? error, string? result, long? logId, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var run = await db.TaskRuns.FindAsync([id], ct); if (run is null) return;
            run.Status = status; run.CompletedTicks = clock.GetUtcNow().UtcTicks; run.Summary = summary; run.Error = error; run.ResultJson = result; run.InvocationLogId = logId;
            var plan = await db.ScheduledTasks.FindAsync([run.TaskId], ct); if (plan is not null) plan.LastRunStatus = status;
            await db.SaveChangesAsync(ct);
        }
        finally { writes.Release(); }
    }
    public async Task<TaskRunView?> CancelAsync(Guid id, CancellationToken ct)
    {
        await writes.WaitAsync(ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var run = await db.TaskRuns.FindAsync([id], ct); if (run is null) return null;
            if (run.Status == "queued") CancelQueued(run, "管理员取消。");
            else if (run.Status == "running") { run.CancelRequested = true; if (active.TryGetValue(id, out var holder)) holder.Cancellation.Cancel(); }
            await db.SaveChangesAsync(ct); return View(run);
        }
        finally { writes.Release(); }
    }
    public async Task DrainAsync(CancellationToken ct)
    {
        stopping = true;
        await dispatch.WaitAsync(CancellationToken.None);
        Task[] executions;
        try { executions = active.Values.Select(a => a.Completion).ToArray(); }
        finally { dispatch.Release(); }
        try { await Task.WhenAll(executions).WaitAsync(TimeSpan.FromSeconds(120), ct); }
        catch (Exception e) when (e is TimeoutException or OperationCanceledException) { await forceStop.CancelAsync(); }
        await plugins.StopAsync(ct);
    }
    public async Task<SchedulerPage<ScheduledTaskView>> ListAsync(int page, int pageSize, string? pluginId, string? status, CancellationToken ct)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await database.CreateDbContextAsync(ct);
        var query = db.ScheduledTasks.AsNoTracking().Where(p => !p.IsDeleted);
        if (!string.IsNullOrWhiteSpace(pluginId)) query = query.Where(p => p.PluginId == pluginId);
        if (status == "enabled") query = query.Where(p => p.IsEnabled);
        if (status == "disabled") query = query.Where(p => !p.IsEnabled);
        if (status == "blocked") query = query.Where(p => p.BlockedReason != null);
        var count = await query.CountAsync(ct);
        var items = await query.OrderBy(p => p.CreatedTicks).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new(items.Select(View).ToArray(), page, pageSize, count);
    }
    public async Task<ScheduledTaskView?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await database.CreateDbContextAsync(ct);
        var plan = await db.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && !p.IsDeleted, ct);
        return plan is null ? null : View(plan);
    }
    public async Task<IReadOnlyList<PluginScheduleInfo>> GetPlansAsync(string pluginId, CancellationToken cancellationToken)
    {
        await using var db = await database.CreateDbContextAsync(cancellationToken);
        var plans = await db.ScheduledTasks.AsNoTracking().Where(p => p.PluginId == pluginId && !p.IsDeleted).ToListAsync(cancellationToken);
        return plans.Select(p => new PluginScheduleInfo(p.Id, p.PluginId, p.TargetType, p.TargetId, Parse(p.ArgumentsJson), p.ScheduleType,
            p.IntervalMinutes, p.CronExpression, p.TimeZoneId, p.IsEnabled, At(p.NextRunTicks), p.BlockedReason)).ToArray();
    }
    public async Task<SchedulerPage<TaskRunView>> RunsAsync(Guid? taskId, string? pluginId, int page, int pageSize, CancellationToken ct, string? status = null, string? trigger = null, DateTimeOffset? fromUtc = null, DateTimeOffset? toUtc = null, string? ownerKind = null, string? correlationId = null)
    {
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await database.CreateDbContextAsync(ct); var query = db.TaskRuns.AsNoTracking().AsQueryable();
        if (taskId is not null) query = query.Where(r => r.TaskId == taskId);
        if (!string.IsNullOrWhiteSpace(pluginId)) query = query.Where(r => r.PluginId == pluginId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(r => r.Status == status);
        if (!string.IsNullOrWhiteSpace(trigger)) query = query.Where(r => r.Trigger == trigger);
        if (fromUtc is { } from) query = query.Where(r => r.CreatedTicks >= from.UtcTicks);
        if (toUtc is { } to) query = query.Where(r => r.CreatedTicks <= to.UtcTicks);
        if (ownerKind == "module") query = query.Where(r => r.PluginId == "web");
        if (ownerKind == "plugin") query = query.Where(r => r.PluginId != "web");
        if (!string.IsNullOrWhiteSpace(correlationId)) query = query.Where(r => r.CorrelationId == correlationId);
        var count = await query.CountAsync(ct);
        var runs = await query.OrderByDescending(r => r.CreatedTicks).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var planIds = runs.Select(r => r.TaskId).Distinct().ToArray();
        var names = await db.ScheduledTasks.Where(p => planIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var owners = await db.PluginStates.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return new(runs.Select(r => View(r) with { TaskName = names.GetValueOrDefault(r.TaskId), OwnerName = owners.GetValueOrDefault(r.PluginId, Capabilities.Name(r.PluginId)) }).ToArray(), page, pageSize, count);
    }
    public async Task<TaskRunView?> RunDetailAsync(Guid id, CancellationToken ct)
    {
        await using var db = await database.CreateDbContextAsync(ct); var run = await db.TaskRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? null : View(run) with { TaskName = await db.ScheduledTasks.Where(p => p.Id == run.TaskId).Select(p => p.Name).SingleOrDefaultAsync(ct),
            OwnerName = await db.PluginStates.Where(p => p.Id == run.PluginId).Select(p => p.Name).SingleOrDefaultAsync(ct) ?? Capabilities.Name(run.PluginId) };
    }
    public async Task<PluginRunInfo?> GetRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await RunDetailAsync(runId, ct); return run is null ? null : new(run.Id, run.Status, run.Summary, run.Error);
    }
    private ScheduledTaskView View(ScheduledTask p) => new(p.Id, p.Name, p.PluginId, p.TargetType, p.TargetId,
        Parse(p.ArgumentsJson), p.DefinitionVersion, p.TemplateId, p.ScheduleType, p.IntervalMinutes, p.CronExpression, p.TimeZoneId,
        p.IsEnabled, p.IsDeleted, p.TimeoutSeconds, p.MaxRetries, p.RiskConfirmed, At(p.NextRunTicks), p.BlockedReason, p.LastRunStatus, p.MisfireSummary) { OwnerKind = Capabilities.Kind(p.PluginId), OwnerName = Capabilities.Name(p.PluginId), TargetName = Capabilities.TargetName(p.PluginId, p.TargetId) };
    private TaskRunView View(TaskRun r) => new(r.Id, r.TaskId, r.PluginId, r.PluginVersion, r.TargetType, r.TargetId,
        r.DefinitionVersion, Parse(r.ArgumentsJson), r.Trigger, At(r.PlannedTicks), At(r.CreatedTicks), At(r.StartedTicks), At(r.CompletedTicks), At(r.RetryAtTicks),
        r.Status, r.Attempt, r.CancelRequested, r.CorrelationId, r.Summary, r.ResultJson is null ? null : Parse(r.ResultJson),
        r.ArtifactJson is null ? null : JsonSerializer.Deserialize<ArtifactReference>(r.ArtifactJson, Json), r.Error, r.InvocationLogId) { OwnerKind = Capabilities.Kind(r.PluginId), OwnerName = Capabilities.Name(r.PluginId), TargetName = Capabilities.TargetName(r.PluginId, r.TargetId) };
}
public sealed class ScheduledTaskWorker(SchedulerService scheduler, ILogger<ScheduledTaskWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await scheduler.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(error, "Scheduler cycle failed"); }
        }
    }
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await scheduler.DrainAsync(cancellationToken);
    }
}
