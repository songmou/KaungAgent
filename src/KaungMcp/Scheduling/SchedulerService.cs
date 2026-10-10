using System.Collections.Concurrent;
using System.Text.Json;
using Cronos;
using KaungMcp.Data;
using KaungMcp.InvocationLogs;
using KaungMcp.Jewelry;
using KaungMcp.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace KaungMcp.Scheduling;

public sealed class SchedulerService(
    IDbContextFactory<KaungMcpDbContext> database,
    IServiceProvider services,
    IOptions<JewelryOptions> jewelryOptions,
    TimeProvider clock,
    ILogger<SchedulerService> logger)
{
    public const string BrandHandler = "jewelry.collect_brands";
    private static string[] TaskSources(ScheduledTask t) => t.Target == BrandHandler ? JewelryBrands.CollectionSources
        : ParseArguments(t.ArgumentsJson).GetProperty("sourceIds").EnumerateArray().Select(x => x.GetString()!).ToArray();

    private readonly SemaphoreSlim initialization = new(1, 1);
    private readonly SemaphoreSlim parallelism = new(4, 4);
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> taskLocks = new();
    private bool initialized;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static DateTimeOffset At(long ticks) => new(ticks, TimeSpan.Zero);
    private static DateTimeOffset? At(long? ticks) => ticks is null ? null : At(ticks.Value);
    private static JsonElement ParseArguments(string value) => JsonDocument.Parse(value).RootElement.Clone();

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (initialized) return;
        await initialization.WaitAsync(ct);
        try
        {
            if (initialized) return;
            await using var db = await database.CreateDbContextAsync(ct);
            var now = clock.GetUtcNow();
            var existing = await db.ScheduledTasks.ToListAsync(ct);
            var running = await db.ScheduledTaskRuns.Where(x => x.Status == "running").ToListAsync(ct);
            foreach (var run in running)
            {
                run.Status = "interrupted";
                run.CompletedTicks = now.UtcTicks;
                run.Error = "Service restarted while this task was running.";
                var owner = existing.SingleOrDefault(x => x.Id == run.TaskId);
                if (owner is not null) owner.LastRunStatus = "interrupted";
            }
            foreach (var task in existing.Where(x => x.IsEnabled && !x.IsArchived &&
                (x.NextRunTicks is null || x.NextRunTicks <= now.UtcTicks)))
                task.NextRunTicks = Next(task, now).UtcTicks;
            foreach (var task in existing.Where(CanMigrateBrandSeed))
            {
                task.Target = BrandHandler;
                task.ArgumentsJson = "{}";
                task.UpdatedTicks = now.UtcTicks;
            }
            var defaults = new[]
            {
                (Guid.Parse("a47d7911-c216-4f06-a5b6-19919f072101"), "国际金价采集", new[] { "gold_api" }, jewelryOptions.Value.MarketIntervalMinutes),
                (Guid.Parse("a47d7911-c216-4f06-a5b6-19919f072102"), "上海金价采集", new[] { "sge_daily", "sge_benchmark" }, jewelryOptions.Value.MarketIntervalMinutes),
                (Guid.Parse("a47d7911-c216-4f06-a5b6-19919f072103"), "品牌金价采集", new[] { "chowsangsang", "lukfook" }, jewelryOptions.Value.BrandIntervalMinutes)
            };
            foreach (var (id, name, sourceIds, minutes) in defaults)
            {
                // Seed once. An archived or edited seed task is still owned by the administrator.
                if (existing.Any(x => x.Id == id)) continue;
                var last = await db.JewelryCollectionStates.AsNoTracking()
                    .Where(x => sourceIds.Contains(x.SourceId)).Select(x => x.LastAttemptTicks).ToListAsync(ct);
                var latest = last.Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty(0).Max();
                var next = latest == 0 ? now : At(latest).AddMinutes(minutes);
                if (latest != 0 && next <= now) next = now.AddMinutes(minutes);
                db.ScheduledTasks.Add(new ScheduledTask
                {
                    Id = id, Name = name, ExecutionType = "internal_handler",
                    Target = name == "品牌金价采集" ? BrandHandler : "jewelry.collect_sources",
                    ArgumentsJson = name == "品牌金价采集" ? "{}" : JsonSerializer.Serialize(new { sourceIds }, Json),
                    ScheduleType = "interval", IntervalMinutes = minutes,
                    TimeZoneId = "Asia/Shanghai", IsEnabled = jewelryOptions.Value.BackgroundCollection,
                    NextRunTicks = jewelryOptions.Value.BackgroundCollection ? next.UtcTicks : null,
                    CreatedTicks = now.UtcTicks, UpdatedTicks = now.UtcTicks
                });
            }
            await db.SaveChangesAsync(ct);
            initialized = true;
        }
        finally { initialization.Release(); }
    }

    public static bool CanMigrateBrandSeed(ScheduledTask task) =>
        task.Id == Guid.Parse("a47d7911-c216-4f06-a5b6-19919f072103") &&
        task.Name == "品牌金价采集" && task.Description is null && task.IsEnabled && !task.IsArchived &&
        task.CreatedTicks == task.UpdatedTicks && task.ExecutionType == "internal_handler" &&
        task.Target == "jewelry.collect_sources" && task.ScheduleType == "interval" &&
        task.IntervalMinutes == 60 && task.CronExpression is null && task.TimeZoneId == "Asia/Shanghai" &&
        TaskSources(task).SequenceEqual(new[] { "chowsangsang", "lukfook" });

    public static DateTimeOffset Next(ScheduledTask task, DateTimeOffset from)
    {
        if (task.ScheduleType == "interval") return from.AddMinutes(task.IntervalMinutes!.Value);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(task.TimeZoneId);
        var next = CronExpression.Parse(task.CronExpression!, CronFormat.Standard)
            .GetNextOccurrence(from.UtcDateTime, zone)
            ?? throw new ArgumentException("Cron expression has no future occurrence.");
        return new DateTimeOffset(DateTime.SpecifyKind(next, DateTimeKind.Utc));
    }

    public static ScheduleValidationResponse ValidateSchedule(ScheduleValidationRequest request, DateTimeOffset now)
    {
        var zoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? "Asia/Shanghai" : request.TimeZoneId;
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(zoneId); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException("未知时区。"); }
        catch (InvalidTimeZoneException) { throw new ArgumentException("时区数据无效。"); }
        if (request.ScheduleType == "interval")
        {
            if (request.IntervalMinutes is < 1 or > 10080)
                throw new ArgumentException("周期必须为 1 至 10080 分钟。");
        }
        else if (request.ScheduleType == "cron")
        {
            if (string.IsNullOrWhiteSpace(request.CronExpression) ||
                request.CronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 5)
                throw new ArgumentException("请输入标准五段 Cron 表达式。");
            try { _ = CronExpression.Parse(request.CronExpression, CronFormat.Standard); }
            catch (CronFormatException) { throw new ArgumentException("Cron 表达式无效。"); }
        }
        else throw new ArgumentException("未知调度类型。");
        var task = new ScheduledTask { ScheduleType = request.ScheduleType, IntervalMinutes = request.IntervalMinutes,
            CronExpression = request.CronExpression, TimeZoneId = zoneId };
        var occurrences = new DateTimeOffset[5];
        for (var i = 0; i < occurrences.Length; i++) occurrences[i] = Next(task, i == 0 ? now : occurrences[i - 1]);
        return new ScheduleValidationResponse(occurrences);
    }

    public async Task<SchedulerCatalog> CatalogAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        var jewelry = services.GetRequiredService<JewelryService>();
        await using var scope = services.CreateAsyncScope();
        var toolManagement = scope.ServiceProvider.GetRequiredService<McpToolManagementService>();
        var tools = (await toolManagement.GetCatalogAsync(ct)).Categories.SelectMany(x => x.Tools)
            .Select(x => (object)new { x.Name, x.Title, x.Description, x.InputSchema, x.IsEnabled,
                x.ReadOnly, x.Destructive, x.RequiresRiskConfirmation }).ToArray();
        return new SchedulerCatalog(
            [new { id = "jewelry.collect_sources", name = "抓取金价来源", parameterSchema = (object)new { sourceIds = "string[]" } },
             new { id = BrandHandler, name = "品牌金价补齐（官方优先）", parameterSchema = (object)new { } }],
            JewelryCatalog.Sources.Select(x => (object)new { x.Id, x.Name, enabled = jewelry.Enabled(x),
                x.Verification, x.Evidence, x.MinimumIntervalMinutes }).ToArray(), tools,
            ["Asia/Shanghai", "UTC", "America/New_York", "Europe/London"]);
    }

    private async Task<(string? RiskSnapshot, bool Risky)> ValidateTargetAsync(
        ScheduledTaskChange change, Guid? existingId, CancellationToken ct)
    {
        if (change.ExecutionType == "internal_handler")
        {
            if (change.Target is not ("jewelry.collect_sources" or BrandHandler)) throw new ArgumentException("未知内部任务类型。");
            var jewelry = services.GetRequiredService<JewelryService>();
            string[] selected;
            if (change.Target == BrandHandler)
            {
                if (change.Arguments.ValueKind != JsonValueKind.Object || change.Arguments.EnumerateObject().Any())
                    throw new ArgumentException("品牌补齐任务不接受来源参数。");
                selected = JewelryBrands.CollectionSources;
                ValidateMinimumInterval(change, 60);
            }
            else
            {
                if (!change.Arguments.TryGetProperty("sourceIds", out var ids) || ids.ValueKind != JsonValueKind.Array)
                    throw new ArgumentException("请选择金价来源。");
                selected = ids.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : "").ToArray();
                if (selected.Length == 0 || selected.Any(string.IsNullOrWhiteSpace) ||
                    selected.Distinct(StringComparer.Ordinal).Count() != selected.Length)
                    throw new ArgumentException("请选择至少一个不重复的金价来源。");
                if (selected.Any(JewelryBrands.IsFallback)) throw new ArgumentException("第三方品牌来源请使用品牌金价补齐任务。");
            }
            foreach (var id in selected)
            {
                var source = JewelryCatalog.Sources.SingleOrDefault(x => x.Id == id);
                if (change.Target == BrandHandler && source is not null && !jewelry.Enabled(source)) continue;
                if (source is null || !jewelry.Enabled(source))
                    throw new ArgumentException($"来源 {id} 尚未验证或已停用，不能加入采集任务。");
                var schedule = new ScheduledTask { ScheduleType = change.ScheduleType,
                    IntervalMinutes = change.IntervalMinutes, CronExpression = change.CronExpression,
                    TimeZoneId = string.IsNullOrWhiteSpace(change.TimeZoneId) ? "Asia/Shanghai" : change.TimeZoneId };
                var point = clock.GetUtcNow();
                for (var i = 0; i < 100; i++)
                {
                    var next = Next(schedule, point);
                    if (next - point < TimeSpan.FromMinutes(source.MinimumIntervalMinutes) && i > 0)
                        throw new ArgumentException($"来源 {id} 的调度间隔不能短于 {source.MinimumIntervalMinutes} 分钟。");
                    point = next;
                }
            }
            if (change.IsEnabled)
            {
                await using var db = await database.CreateDbContextAsync(ct);
                var others = await db.ScheduledTasks.AsNoTracking().Where(x => x.IsEnabled && !x.IsArchived &&
                    x.ExecutionType == "internal_handler" && (x.Target == "jewelry.collect_sources" || x.Target == BrandHandler) && x.Id != existingId).ToListAsync(ct);
                foreach (var other in others)
                {
                    var otherIds = TaskSources(other).ToHashSet();
                    if (selected.Any(otherIds.Contains)) throw new ArgumentException("已启用的金价任务不能重复配置相同来源。");
                }
            }
            return (null, false);
        }
        if (change.ExecutionType != "mcp_tool") throw new ArgumentException("未知执行类型。");
        await using var scope = services.CreateAsyncScope();
        var toolManagement = scope.ServiceProvider.GetRequiredService<McpToolManagementService>();
        var tool = (await toolManagement.GetCatalogAsync(ct)).Categories.SelectMany(x => x.Tools)
            .SingleOrDefault(x => x.Name == change.Target);
        if (tool is null || !tool.IsEnabled) throw new ArgumentException("MCP 工具不存在或已停用。");
        var snapshot = JsonSerializer.Serialize(new { tool.ReadOnly, tool.Destructive });
        if (tool.RequiresRiskConfirmation && !change.ConfirmRisk)
            throw new ArgumentException("此 MCP 工具可能修改状态，需要明确确认无人值守执行风险。");
        return (snapshot, tool.RequiresRiskConfirmation);
    }

    private void ValidateMinimumInterval(ScheduledTaskChange change, int minimum)
    {
        var task = new ScheduledTask { ScheduleType = change.ScheduleType, IntervalMinutes = change.IntervalMinutes,
            CronExpression = change.CronExpression, TimeZoneId = change.TimeZoneId ?? "Asia/Shanghai" };
        var point = clock.GetUtcNow();
        for (var i = 0; i < 100; i++)
        {
            var next = Next(task, point);
            if (i > 0 && next - point < TimeSpan.FromMinutes(minimum))
                throw new ArgumentException($"品牌补齐调度间隔不能短于 {minimum} 分钟。");
            point = next;
        }
    }

    public async Task<ScheduledTaskView> CreateAsync(ScheduledTaskChange change, CancellationToken ct)
    {
        await InitializeAsync(ct);
        ValidateChange(change);
        var (risk, risky) = await ValidateTargetAsync(change, null, ct);
        var now = clock.GetUtcNow();
        var task = new ScheduledTask { Id = Guid.NewGuid(), CreatedTicks = now.UtcTicks };
        Apply(task, change, now, risk, risky);
        await using var db = await database.CreateDbContextAsync(ct);
        db.ScheduledTasks.Add(task);
        await db.SaveChangesAsync(ct);
        return View(task);
    }

    public async Task<ScheduledTaskView?> UpdateAsync(Guid id, ScheduledTaskChange change, CancellationToken ct)
    {
        await InitializeAsync(ct);
        ValidateChange(change);
        await using var db = await database.CreateDbContextAsync(ct);
        var task = await db.ScheduledTasks.FindAsync([id], ct);
        if (task is null || task.IsArchived) return null;
        var (risk, risky) = await ValidateTargetAsync(change, id, ct);
        Apply(task, change, clock.GetUtcNow(), risk, risky);
        await db.SaveChangesAsync(ct);
        return View(task);
    }

    private static void ValidateChange(ScheduledTaskChange change)
    {
        if (string.IsNullOrWhiteSpace(change.Name) || change.Name.Length > 120 ||
            change.Description?.Length > 1000 || change.Target?.Length is < 1 or > 200 ||
            change.Arguments.ValueKind != JsonValueKind.Object || change.Arguments.GetRawText().Length > 65536)
            throw new ArgumentException("任务名称、目标或参数无效。");
        _ = ValidateSchedule(new ScheduleValidationRequest(change.ScheduleType, change.IntervalMinutes,
            change.CronExpression, change.TimeZoneId), DateTimeOffset.UtcNow);
    }

    private static void Apply(ScheduledTask task, ScheduledTaskChange change, DateTimeOffset now, string? risk, bool risky)
    {
        task.Name = change.Name.Trim(); task.Description = change.Description?.Trim();
        task.ExecutionType = change.ExecutionType; task.Target = change.Target;
        task.ArgumentsJson = change.Arguments.GetRawText();
        task.ScheduleType = change.ScheduleType; task.IntervalMinutes = change.ScheduleType == "interval" ? change.IntervalMinutes : null;
        task.CronExpression = change.ScheduleType == "cron" ? change.CronExpression!.Trim() : null;
        task.TimeZoneId = string.IsNullOrWhiteSpace(change.TimeZoneId) ? "Asia/Shanghai" : change.TimeZoneId;
        task.IsEnabled = change.IsEnabled; task.RiskSnapshot = risk;
        task.RiskConfirmed = risky && change.ConfirmRisk;
        task.UpdatedTicks = now.UtcTicks;
        task.NextRunTicks = change.IsEnabled ? Next(task, now).UtcTicks : null;
    }

    public async Task<ScheduledTaskPage> ListAsync(int page, int pageSize, string? status, string? executionType,
        string? target, CancellationToken ct)
    {
        await InitializeAsync(ct);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await database.CreateDbContextAsync(ct);
        var query = db.ScheduledTasks.AsNoTracking().AsQueryable();
        if (status == "archived") query = query.Where(x => x.IsArchived);
        else if (status == "enabled") query = query.Where(x => x.IsEnabled && !x.IsArchived);
        else if (status == "disabled") query = query.Where(x => !x.IsEnabled && !x.IsArchived);
        else if (status == "active" || string.IsNullOrEmpty(status)) query = query.Where(x => !x.IsArchived);
        else if (status != "all") throw new ArgumentException("未知任务状态。");
        if (!string.IsNullOrWhiteSpace(executionType)) query = query.Where(x => x.ExecutionType == executionType);
        if (!string.IsNullOrWhiteSpace(target)) query = query.Where(x => x.Target.Contains(target));
        var count = await query.CountAsync(ct);
        var items = await query.OrderBy(x => x.IsArchived).ThenBy(x => x.NextRunTicks)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var enabled = await db.ScheduledTasks.CountAsync(x => x.IsEnabled && !x.IsArchived, ct);
        var running = await db.ScheduledTaskRuns.CountAsync(x => x.Status == "running", ct);
        var cutoff = clock.GetUtcNow().AddDays(-1).UtcTicks;
        var failed = await db.ScheduledTaskRuns.CountAsync(x => x.StartedTicks >= cutoff &&
            (x.Status == "failed" || x.Status == "blocked"), ct);
        return new ScheduledTaskPage(items.Select(View).ToArray(), page, pageSize, count, enabled, running, failed);
    }

    public async Task<ScheduledTaskView?> GetAsync(Guid id, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var db = await database.CreateDbContextAsync(ct);
        var task = await db.ScheduledTasks.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return task is null ? null : View(task);
    }

    public async Task<ScheduledTaskView?> SetStateAsync(Guid id, string action, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var db = await database.CreateDbContextAsync(ct);
        var task = await db.ScheduledTasks.FindAsync([id], ct);
        if (task is null) return null;
        if (action == "restore") { task.IsArchived = false; task.IsEnabled = false; }
        else if (task.IsArchived) throw new ArgumentException("请先恢复已归档任务。");
        else if (action == "archive") { task.IsArchived = true; task.IsEnabled = false; }
        else if (action == "enable")
        {
            var change = new ScheduledTaskChange(task.Name, task.Description, task.ExecutionType, task.Target,
                ParseArguments(task.ArgumentsJson), task.ScheduleType, task.IntervalMinutes, task.CronExpression,
                task.TimeZoneId, true, task.RiskConfirmed);
            await ValidateTargetAsync(change, id, ct);
            task.IsEnabled = true;
        }
        else if (action == "disable") task.IsEnabled = false;
        else throw new ArgumentException("未知任务操作。");
        var now = clock.GetUtcNow();
        task.NextRunTicks = task.IsEnabled ? Next(task, now).UtcTicks : null;
        task.UpdatedTicks = now.UtcTicks;
        await db.SaveChangesAsync(ct);
        return View(task);
    }

    public async Task<ScheduledRunPage> RunsAsync(Guid taskId, int page, int pageSize, CancellationToken ct)
    {
        await InitializeAsync(ct);
        page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
        await using var db = await database.CreateDbContextAsync(ct);
        var query = db.ScheduledTaskRuns.AsNoTracking().Where(x => x.TaskId == taskId);
        var count = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.StartedTicks).ThenByDescending(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new ScheduledRunPage(rows.Select(View).ToArray(), page, pageSize, count);
    }

    public async Task<ScheduledRunView?> RunDetailAsync(long id, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var db = await database.CreateDbContextAsync(ct);
        var run = await db.ScheduledTaskRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        return run is null ? null : View(run);
    }

    public async Task TickAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var db = await database.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcTicks;
        var due = await db.ScheduledTasks.AsNoTracking().Where(x => x.IsEnabled && !x.IsArchived &&
            x.NextRunTicks != null && x.NextRunTicks <= now).Select(x => x.Id).ToListAsync(ct);
        await Task.WhenAll(due.Select(id => RunAsync(id, "scheduled", false, ct)));
    }

    public async Task CollectOnQueryAsync(IEnumerable<string> sourceIds, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var db = await database.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow().UtcTicks;
        var selected = sourceIds.ToHashSet(StringComparer.Ordinal);
        var tasks = await db.ScheduledTasks.AsNoTracking().Where(x => x.IsEnabled && !x.IsArchived &&
            x.ExecutionType == "internal_handler" && (x.Target == "jewelry.collect_sources" || x.Target == BrandHandler)).ToListAsync(ct);
        foreach (var task in tasks)
        {
            var ids = TaskSources(task);
            if (!ids.Any(id => selected.Contains(id!))) continue;
            var relevant = ids.Where(selected.Contains).ToArray();
            var cached = await db.JewelryQuotes.AsNoTracking().Where(x => relevant.Contains(x.SourceId))
                .Select(x => x.SourceId).Distinct().ToListAsync(ct);
            var noCache = task.Target == BrandHandler
                ? !await db.JewelryQuotes.AsNoTracking().AnyAsync(q => q.InstrumentId.StartsWith("BRAND:"), ct)
                : relevant.Any(id => !cached.Contains(id));
            if (noCache || task.NextRunTicks <= now)
                await RunAsync(task.Id, "query_catchup", false, ct);
        }
    }

    public async Task<Dictionary<string, ScheduledTask>> JewelrySchedulesAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var db = await database.CreateDbContextAsync(ct);
        var tasks = await db.ScheduledTasks.AsNoTracking().Where(x => x.IsEnabled && !x.IsArchived &&
            x.ExecutionType == "internal_handler" && (x.Target == "jewelry.collect_sources" || x.Target == BrandHandler)).ToListAsync(ct);
        var result = new Dictionary<string, ScheduledTask>(StringComparer.Ordinal);
        foreach (var task in tasks)
            foreach (var id in TaskSources(task))
                result[id] = task;
        return result;
    }

    public async Task<ScheduledRunView?> RunAsync(Guid id, string trigger, bool force, CancellationToken ct)
    {
        await InitializeAsync(ct);
        if (trigger is not ("scheduled" or "manual" or "query_catchup")) throw new ArgumentException("未知触发来源。");
        var gate = taskLocks.GetOrAdd(id, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, ct)) return await RecordSkippedAsync(id, trigger, "Task is already running.", ct);
        try
        {
            await using var db = await database.CreateDbContextAsync(ct);
            var task = await db.ScheduledTasks.FindAsync([id], ct);
            if (task is null) return null;
            if (task.IsArchived) throw new ArgumentException("已归档任务不能执行。");
            if (trigger != "manual" && !task.IsEnabled) return null;
            var now = clock.GetUtcNow();
            if (trigger == "scheduled" && task.NextRunTicks > now.UtcTicks) return null;
            var configurationVersion = task.UpdatedTicks;
            var run = new ScheduledTaskRun { TaskId = id, Trigger = trigger,
                PlannedTicks = trigger == "scheduled" ? task.NextRunTicks : null,
                StartedTicks = now.UtcTicks };
            db.ScheduledTaskRuns.Add(run);
            await db.SaveChangesAsync(ct);
            try { await parallelism.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                run.Status = "interrupted";
                run.CompletedTicks = clock.GetUtcNow().UtcTicks;
                run.Error = "Execution was cancelled before a worker slot became available.";
                await db.SaveChangesAsync(CancellationToken.None);
                return View(run);
            }
            SchedulerRunOutcome outcome;
            try { outcome = await ExecuteAsync(task, force || trigger == "manual", ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { outcome = new SchedulerRunOutcome("interrupted", null, "Execution was cancelled."); }
            catch (Exception error)
            {
                logger.LogError(error, "Scheduled task {TaskId} failed", id);
                outcome = new SchedulerRunOutcome("failed", null, error.Message);
            }
            finally { parallelism.Release(); }
            var finished = clock.GetUtcNow();
            run.Status = outcome.Status; run.CompletedTicks = finished.UtcTicks;
            run.ResultSummary = outcome.Summary?[..Math.Min(outcome.Summary.Length, 2000)];
            run.Error = outcome.Error?[..Math.Min(outcome.Error.Length, 1000)];
            run.InvocationLogId = outcome.InvocationLogId;
            await db.Entry(task).ReloadAsync(CancellationToken.None);
            task.LastRunStatus = run.Status;
            if (task.UpdatedTicks == configurationVersion && task.IsEnabled && !task.IsArchived &&
                !(trigger == "query_catchup" && run.Status == "skipped"))
                task.NextRunTicks = Next(task, finished).UtcTicks;
            await db.SaveChangesAsync(CancellationToken.None);
            return View(run);
        }
        finally { gate.Release(); }
    }

    private async Task<ScheduledRunView?> RecordSkippedAsync(Guid id, string trigger, string reason, CancellationToken ct)
    {
        await using var db = await database.CreateDbContextAsync(ct);
        if (!await db.ScheduledTasks.AnyAsync(x => x.Id == id, ct)) return null;
        var now = clock.GetUtcNow().UtcTicks;
        var run = new ScheduledTaskRun { TaskId = id, Trigger = trigger, StartedTicks = now,
            CompletedTicks = now, Status = "skipped", Error = reason };
        db.ScheduledTaskRuns.Add(run);
        await db.SaveChangesAsync(ct);
        return View(run);
    }

    private async Task<SchedulerRunOutcome> ExecuteAsync(ScheduledTask task, bool force, CancellationToken ct)
    {
        if (task.ExecutionType == "internal_handler")
        {
            var jewelry = services.GetRequiredService<JewelryService>();
            var results = task.Target == BrandHandler ? await jewelry.CollectBrandsAsync(force, ct)
                : await jewelry.CollectScheduledAsync(TaskSources(task), force, ct);
            var ok = results.Count(x => x.Status == "succeeded");
            var failed = results.Count(x => x.Status == "failed");
            var status = failed == 0 ? ok == 0 ? "skipped" : "succeeded" : ok == 0 ? "failed" : "partial";
            return new SchedulerRunOutcome(status, JsonSerializer.Serialize(results, Json),
                failed == 0 ? null : string.Join("; ", results.Where(x => x.Status == "failed").Select(x => $"{x.SourceId}: {x.Error}")));
        }
        await using var scope = services.CreateAsyncScope();
        var toolManagement = scope.ServiceProvider.GetRequiredService<McpToolManagementService>();
        var tool = (await toolManagement.GetCatalogAsync(ct)).Categories.SelectMany(x => x.Tools)
            .SingleOrDefault(x => x.Name == task.Target);
        if (tool is null || !tool.IsEnabled)
            return new SchedulerRunOutcome("blocked", null, "MCP tool is missing or disabled.");
        var snapshot = JsonSerializer.Serialize(new { tool.ReadOnly, tool.Destructive });
        if (snapshot != task.RiskSnapshot || tool.RequiresRiskConfirmation && !task.RiskConfirmed)
            return new SchedulerRunOutcome("blocked", null, "Tool risk declaration changed; edit the task and confirm again.");
        var result = await toolManagement.TestAsync(task.Target, ParseArguments(task.ArgumentsJson),
            task.RiskConfirmed, ct, InvocationSources.Scheduler);
        return new SchedulerRunOutcome(result!.Succeeded ? "succeeded" : "failed",
            $"MCP tool {task.Target}: {(result.Succeeded ? "succeeded" : "failed")}", result.Error, result.LogId);
    }

    private static ScheduledTaskView View(ScheduledTask x) => new(x.Id, x.Name, x.Description,
        x.ExecutionType, x.Target, ParseArguments(x.ArgumentsJson), x.ScheduleType, x.IntervalMinutes,
        x.CronExpression, x.TimeZoneId, x.IsEnabled, x.IsArchived, x.RiskConfirmed, x.RiskSnapshot,
        At(x.NextRunTicks), x.LastRunStatus, At(x.CreatedTicks), At(x.UpdatedTicks));
    private static ScheduledRunView View(ScheduledTaskRun x) => new(x.Id, x.TaskId, x.Trigger,
        At(x.PlannedTicks), At(x.StartedTicks), At(x.CompletedTicks), x.CompletedTicks is null ? null :
        (x.CompletedTicks.Value - x.StartedTicks) / TimeSpan.TicksPerMillisecond,
        x.Status, x.Error, x.ResultSummary, x.InvocationLogId);
}

public sealed class ScheduledTaskWorker(SchedulerService scheduler, ILogger<ScheduledTaskWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await scheduler.InitializeAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try { await scheduler.TickAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception error) { logger.LogError(error, "Scheduler cycle failed."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
