using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using KaungMcp.Data;
using KaungMcp.Plugin;
using KaungMcp.Plugins;
using KaungMcp.Scheduling;
using KaungMcp.Security;
using KaungMcp.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace KaungMcp.Tests;

public sealed class PluginSchedulerTests
{
    private static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });
    [Fact]
    public void Schedules_validate_timezone_and_preserve_interval_anchor()
    {
        var now = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
        var cron = SchedulerService.ValidateSchedule(new("cron", null, "0 9 * * *", "Asia/Shanghai"), now);
        Assert.Equal(now.AddHours(1), cron.NextOccurrencesUtc[0]);
        Assert.Equal(now.AddMinutes(15), SchedulerService.Next(new("interval", 15, null, "UTC"), now));
        Assert.Throws<ArgumentException>(() => SchedulerService.Next(new("cron", null, "bad", "UTC"), now));
        Assert.Throws<ArgumentException>(() => SchedulerService.Next(new("interval", 0, null, "UTC"), now));
        Assert.Throws<ArgumentException>(() => SchedulerService.Next(new("interval", 15, null, "invalid"), now));
        var dst = SchedulerService.ValidateSchedule(new("cron", null, "30 1 * * *", "America/New_York"), DateTimeOffset.Parse("2026-11-01T00:00:00Z"));
        Assert.Equal(5, dst.NextOccurrencesUtc.Distinct().Count());
        Assert.True(dst.NextOccurrencesUtc.Zip(dst.NextOccurrencesUtc.Skip(1)).All(p => p.Second > p.First));
    }
    [Fact]
    public async Task Default_plans_are_seeded_once_and_deleted_templates_never_return()
    {
        await using var factory = new McpWebApplicationFactory();
        using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plans = (await scheduler.ListAsync(1, 20, "jewelry", null, default)).Items;
        Assert.Equal(3, plans.Count);
        Assert.Equal(new[] { 15, 15, 60 }, plans.Select(p => p.IntervalMinutes!.Value).Order().ToArray());
        var first = plans[0];
        await scheduler.UpdateAsync(first.Id, ToChange(first) with { Name = "用户修改", IsEnabled = false }, default);
        await scheduler.SetStateAsync(plans[1].Id, "delete", default);
        var restarted = Restart(factory);
        await restarted.InitializeAsync(default);
        await restarted.InitializeAsync(default);
        var remaining = (await restarted.ListAsync(1, 20, "jewelry", null, default)).Items;
        Assert.Equal(2, remaining.Count);
        Assert.Contains(remaining, p => p.Name == "用户修改" && !p.IsEnabled);
        await using var db = await Database(factory).CreateDbContextAsync();
        Assert.Equal(3, await db.ScheduledTasks.CountAsync());
        Assert.Single(await db.ScheduledTasks.Where(p => p.IsDeleted).ToListAsync());
    }
    [Fact]
    public async Task Due_runs_are_unique_and_interval_advances_from_planned_time()
    {
        var clock = new TestClock(); var state = new HandlerState();
        await using var factory = Fixture(state, clock); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = await scheduler.CreateAsync(HandlerPlan(), default);
        clock.Advance(TimeSpan.FromMinutes(16));
        await scheduler.TickAsync(default); await scheduler.TickAsync(default);
        var runs = await scheduler.RunsAsync(plan.Id, null, 1, 20, default);
        var run = Assert.Single(runs.Items);
        Assert.Equal(plan.NextRunAtUtc, run.PlannedAtUtc);
        Assert.Equal(clock.Origin.AddMinutes(30), (await scheduler.GetAsync(plan.Id, default))!.NextRunAtUtc);
        await WaitTerminal(scheduler, run.Id);
        Assert.Equal(1, state.Calls);
    }
    [Fact]
    public async Task Manual_execution_of_disabled_plan_is_async_and_snapshots_arguments()
    {
        var state = new HandlerState { Block = true };
        await using var factory = Fixture(state); using var admin = Admin(factory);
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = await scheduler.CreateAsync(HandlerPlan() with { IsEnabled = false, Arguments = JsonSerializer.SerializeToElement(new { message = "snapshot" }) }, default);
        var response = await admin.PostAsync($"/api/admin/scheduled-tasks/{plan.Id}/run", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetGuid();
        Assert.Equal("queued", (await scheduler.RunDetailAsync(id, default))!.Status);
        await scheduler.DispatchAsync(default); await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var running = (await scheduler.RunDetailAsync(id, default))!;
        Assert.Equal("running", running.Status); Assert.Equal("snapshot", running.Arguments.GetProperty("message").GetString());
        var second = await scheduler.SubmitAsync(plan.Id, "manual", default);
        Assert.Equal("skipped", (await scheduler.RunDetailAsync(second, default))!.Status);
        state.Release.TrySetResult();
        var completed = await WaitTerminal(scheduler, id);
        Assert.Equal("succeeded", completed.Status); Assert.Equal("1.0.0", completed.PluginVersion);
        Assert.NotNull(completed.InvocationLogId);
        await using var db = await Database(factory).CreateDbContextAsync();
        Assert.Equal(completed.CorrelationId, (await db.InvocationLogs.SingleAsync(l => l.Id == completed.InvocationLogId)).CorrelationId);
    }
    [Fact]
    public async Task Disabling_plugin_cancels_queue_but_allows_running_work_to_finish()
    {
        var state = new HandlerState { Block = true };
        await using var factory = Fixture(state); using var admin = Admin(factory);
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var first = await scheduler.CreateAsync(HandlerPlan(), default);
        var second = await scheduler.CreateAsync(HandlerPlan() with { Name = "second" }, default);
        var running = await scheduler.SubmitAsync(first.Id, "manual", default);
        await scheduler.DispatchAsync(default); await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var queued = await scheduler.SubmitAsync(second.Id, "manual", default);
        (await admin.PutAsJsonAsync("/api/admin/plugins/diagnostics/enabled", new { isEnabled = false })).EnsureSuccessStatusCode();
        Assert.Equal("cancelled", (await scheduler.RunDetailAsync(queued, default))!.Status);
        Assert.Equal("running", (await scheduler.RunDetailAsync(running, default))!.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.SubmitAsync(second.Id, "manual", default));
        var test = await admin.PostAsJsonAsync("/api/admin/tools/kaung_echo/test", new { arguments = new { message = "blocked" } });
        Assert.Equal(HttpStatusCode.BadRequest, test.StatusCode);
        state.Release.TrySetResult(); Assert.Equal("succeeded", (await WaitTerminal(scheduler, running)).Status);
        (await admin.PutAsJsonAsync("/api/admin/plugins/diagnostics/enabled", new { isEnabled = true })).EnsureSuccessStatusCode();
        Assert.Null((await scheduler.GetAsync(first.Id, default))!.BlockedReason);
    }
    [Fact]
    public async Task Cancellation_does_not_report_completion_until_handler_returns()
    {
        var state = new HandlerState { Block = true, IgnoreCancellation = true };
        await using var factory = Fixture(state); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = await scheduler.CreateAsync(HandlerPlan(), default);
        var id = await scheduler.SubmitAsync(plan.Id, "manual", default); await scheduler.DispatchAsync(default);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var pending = (await scheduler.CancelAsync(id, default))!;
        Assert.True(pending.CancelRequested); Assert.Equal("running", pending.Status); Assert.Null(pending.CompletedAtUtc);
        state.Release.TrySetResult(); var finished = await WaitTerminal(scheduler, id);
        Assert.Equal("cancelled", finished.Status); Assert.NotNull(finished.InvocationLogId);
    }
    [Fact]
    public async Task Cooperative_timeout_is_terminal_and_not_retried()
    {
        var state = new HandlerState { Block = true };
        await using var factory = Fixture(state); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = await scheduler.CreateAsync(HandlerPlan() with { TimeoutSeconds = 1, MaxRetries = 2 }, default);
        var id = await scheduler.SubmitAsync(plan.Id, "manual", default); await scheduler.DispatchAsync(default);
        var timedOut = await WaitTerminal(scheduler, id);
        Assert.Equal("timed_out", timedOut.Status); Assert.Equal(1, state.Calls);
        Assert.NotNull(timedOut.InvocationLogId);
        await using var db = await Database(factory).CreateDbContextAsync();
        Assert.Equal("timed_out", (await db.InvocationLogs.FindAsync(timedOut.InvocationLogId))!.ErrorCode);
    }
    [Fact]
    public async Task Resource_keys_and_plugin_capacity_limit_parallel_work()
    {
        var state = new HandlerState { Block = true, SharedKey = true };
        await using var factory = Fixture(state); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var first = await scheduler.CreateAsync(HandlerPlan(), default);
        var second = await scheduler.CreateAsync(HandlerPlan() with { Name = "second" }, default);
        var a = await scheduler.SubmitAsync(first.Id, "manual", default);
        var b = await scheduler.SubmitAsync(second.Id, "manual", default);
        await scheduler.DispatchAsync(default); await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, state.Calls); Assert.Equal("queued", (await scheduler.RunDetailAsync(b, default))!.Status);
        state.Release.TrySetResult(); await WaitTerminal(scheduler, a); await scheduler.DispatchAsync(default);
        Assert.Equal("succeeded", (await WaitTerminal(scheduler, b)).Status);
    }
    [Fact]
    public async Task Idempotent_failure_retries_on_persisted_deadlines_with_same_run_id()
    {
        var clock = new TestClock(); var state = new HandlerState { Failures = 2 };
        await using var factory = Fixture(state, clock); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = await scheduler.CreateAsync(HandlerPlan() with { MaxRetries = 2 }, default);
        var id = await scheduler.SubmitAsync(plan.Id, "manual", default); await scheduler.DispatchAsync(default);
        var retry = await WaitAttempt(scheduler, id, 1);
        Assert.Equal("queued", retry.Status); Assert.Equal(clock.Origin.AddSeconds(30), retry.RetryAtUtc);
        await scheduler.DispatchAsync(default); Assert.Equal(1, state.Calls);
        clock.Advance(TimeSpan.FromSeconds(30)); await scheduler.DispatchAsync(default);
        retry = await WaitAttempt(scheduler, id, 2); Assert.Equal(clock.GetUtcNow().AddSeconds(120), retry.RetryAtUtc);
        clock.Advance(TimeSpan.FromSeconds(120)); await scheduler.DispatchAsync(default);
        Assert.Equal("succeeded", (await WaitTerminal(scheduler, id)).Status); Assert.Equal(3, state.Calls);
        Assert.Single((await scheduler.RunsAsync(plan.Id, null, 1, 20, default)).Items);
    }
    [Fact]
    public async Task Restart_marks_inflight_interrupted_preserves_queue_and_skips_missed_points()
    {
        var clock = new TestClock(); var state = new HandlerState();
        await using var factory = Fixture(state, clock); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var first = await scheduler.CreateAsync(HandlerPlan(), default);
        var second = await scheduler.CreateAsync(HandlerPlan() with { Name = "queued" }, default);
        var runningId = await scheduler.SubmitAsync(first.Id, "manual", default);
        var queuedId = await scheduler.SubmitAsync(second.Id, "manual", default);
        await using (var db = await Database(factory).CreateDbContextAsync())
        { var run = await db.TaskRuns.SingleAsync(r => r.Id == runningId); run.Status = "running"; await db.SaveChangesAsync(); }
        clock.Advance(TimeSpan.FromHours(2)); var restarted = Restart(factory); await restarted.InitializeAsync(default);
        Assert.Equal("interrupted", (await restarted.RunDetailAsync(runningId, default))!.Status);
        Assert.NotNull((await restarted.GetAsync(first.Id, default))!.MisfireSummary);
        Assert.Equal("queued", (await restarted.RunDetailAsync(queuedId, default))!.Status);
        await restarted.DispatchAsync(default); Assert.Equal("succeeded", (await WaitTerminal(restarted, queuedId)).Status);
        Assert.Equal(2, (await restarted.RunsAsync(null, null, 1, 20, default)).TotalCount);
    }
    [Fact]
    public async Task Changed_handler_version_blocks_existing_plans_and_queue_until_edit()
    {
        await using var factory = Fixture(new()); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = await scheduler.CreateAsync(HandlerPlan(), default);
        var runtime = factory.Services.GetRequiredService<PluginRuntime>();
        var tasks = runtime.Plugins.Single(p => p.Manifest.Id == "diagnostics").Registration!.Tasks;
        tasks[0] = tasks[0] with { Version = 2 };
        await scheduler.TickAsync(default); Assert.NotNull((await scheduler.GetAsync(plan.Id, default))!.BlockedReason);
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.SubmitAsync(plan.Id, "manual", default));
        var edited = await scheduler.UpdateAsync(plan.Id, HandlerPlan(), default);
        Assert.Equal(2, edited!.DefinitionVersion); Assert.Null(edited.BlockedReason);
    }
    [Fact]
    public async Task Tool_targets_require_opt_in_validate_arguments_and_reconfirm_risk_drift()
    {
        await using var factory = Fixture(new()); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var change = new ScheduledTaskChange("echo", "diagnostics", "tool", "kaung_echo", JsonSerializer.SerializeToElement(new { message = "hello" }));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(change with { Arguments = Empty }, default));
        var tool = factory.Services.GetServices<McpServerTool>().Single(t => t.ProtocolTool.Name == "kaung_echo");
        tool.ProtocolTool.Annotations!.ReadOnlyHint = false;
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(change, default));
        var plan = await scheduler.CreateAsync(change with { ConfirmRisk = true }, default);
        tool.ProtocolTool.Annotations.DestructiveHint = true;
        await scheduler.TickAsync(default); Assert.NotNull((await scheduler.GetAsync(plan.Id, default))!.BlockedReason);
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.SubmitAsync(plan.Id, "manual", default));
        var runtime = factory.Services.GetRequiredService<PluginRuntime>();
        var policy = runtime.Plugins.Single(p => p.Manifest.Id == "diagnostics").Registration!.Tools;
        // Registered policies are immutable at runtime; a non-schedulable internal tool is tested via jewelry elsewhere.
        Assert.Contains(policy, p => p.Name == "kaung_echo" && p.Schedulable);
    }
    [Fact]
    public async Task Queued_cancellation_and_plan_updates_preserve_history()
    {
        await using var factory = Fixture(new()); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = await scheduler.CreateAsync(HandlerPlan(), default);
        var id = await scheduler.SubmitAsync(plan.Id, "manual", default);
        await scheduler.UpdateAsync(plan.Id, HandlerPlan() with { Arguments = JsonSerializer.SerializeToElement(new { message = "new" }) }, default);
        var cancelled = (await scheduler.RunDetailAsync(id, default))!;
        Assert.Equal("cancelled", cancelled.Status); Assert.Equal("{}", cancelled.Arguments.GetRawText());
        var second = await scheduler.SubmitAsync(plan.Id, "manual", default); await scheduler.CancelAsync(second, default);
        await scheduler.DispatchAsync(default); Assert.Equal("cancelled", (await scheduler.RunDetailAsync(second, default))!.Status);
    }
    [Fact]
    public async Task Independent_jewelry_database_and_handler_validation_are_owned_by_plugin()
    {
        await using var factory = new McpWebApplicationFactory(pluginIds: ["jewelry"]); using var client = factory.CreateClient();
        await using var db = await Database(factory).CreateDbContextAsync();
        Assert.DoesNotContain(db.Model.GetEntityTypes(), e => e.Name.Contains("Jewelry", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(factory.DatabasePath + ".plugins", "jewelry", "plugin.db")));
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plans = (await scheduler.ListAsync(1, 20, null, null, default)).Items;
        var market = plans.First(p => p.TargetId == "collect_market");
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.UpdateAsync(market.Id, ToChange(market) with { IntervalMinutes = 1 }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(ToChange(market) with { Name = "duplicate" }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(new("internal", "jewelry", "tool", "kaung_jewelry_get_source_status", Empty), default));
    }
    [Fact]
    public async Task Global_and_per_plugin_limits_apply_to_distinct_resources()
    {
        var state = new HandlerState { Block = true };
        await using var factory = Fixture(state, pluginIds: ["diagnostics", "jewelry"]); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var runs = new List<Guid>();
        foreach (var pluginId in new[] { "diagnostics", "jewelry" })
            for (var i = 0; i < 3; i++)
            {
                var plan = await scheduler.CreateAsync(HandlerPlan() with { Name = pluginId + i, PluginId = pluginId, Arguments = JsonSerializer.SerializeToElement(new { message = pluginId }) }, default);
                runs.Add(await scheduler.SubmitAsync(plan.Id, "manual", default));
            }
        await scheduler.DispatchAsync(default);
        for (var i = 0; i < 500 && state.Calls < 4; i++) await Task.Delay(10);
        Assert.Equal(4, state.Calls);
        Assert.Equal(2, state.CallsByPlugin["diagnostics"]); Assert.Equal(2, state.CallsByPlugin["jewelry"]);
        state.Release.TrySetResult();
        for (var i = 0; i < 500 && state.Calls < 6; i++) { await scheduler.DispatchAsync(default); await Task.Delay(10); }
        Assert.Equal(6, state.Calls);
        foreach (var id in runs) Assert.Equal("succeeded", (await WaitTerminal(scheduler, id)).Status);
    }
    [Fact]
    public async Task Handler_exceptions_are_logged_and_shutdown_drains_running_work()
    {
        var state = new HandlerState { Block = true, Throw = true };
        await using var factory = Fixture(state); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = await scheduler.CreateAsync(HandlerPlan(), default);
        var id = await scheduler.SubmitAsync(plan.Id, "manual", default); await scheduler.DispatchAsync(default);
        await state.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var drain = scheduler.DrainAsync(default);
        Assert.False(drain.IsCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.SubmitAsync(plan.Id, "manual", default));
        state.Release.TrySetResult(); await drain.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await WaitTerminal(scheduler, id);
        Assert.Equal("failed", result.Status); Assert.NotNull(result.InvocationLogId); Assert.Contains("fixture exception", result.Error);
    }
    [Fact]
    public async Task Default_templates_support_cron_and_do_not_override_user_edits()
    {
        await using var factory = Fixture(new(), addCronTemplate: true); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = Assert.Single((await scheduler.ListAsync(1, 20, null, null, default)).Items);
        Assert.Equal("cron", plan.ScheduleType); Assert.Equal("0 * * * *", plan.CronExpression);
        await scheduler.UpdateAsync(plan.Id, ToChange(plan) with { Name = "edited", CronExpression = "0 8 * * *" }, default);
        await Restart(factory).InitializeAsync(default);
        Assert.Equal("edited", (await scheduler.GetAsync(plan.Id, default))!.Name);
        Assert.Equal("0 8 * * *", (await scheduler.GetAsync(plan.Id, default))!.CronExpression);
    }
    [Fact]
    public async Task Default_business_validation_failure_blocks_plan_without_stopping_host()
    {
        await using var factory = Fixture(new() { ValidationThrow = true }, addCronTemplate: true); using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = Assert.Single((await scheduler.ListAsync(1, 20, null, null, default)).Items);
        Assert.Contains("validation fixture", plan.BlockedReason);
        await scheduler.TickAsync(default);
        Assert.Empty((await scheduler.RunsAsync(plan.Id, null, 1, 20, default)).Items);
        Assert.True(factory.Services.GetRequiredService<PluginRuntime>().Available("diagnostics"));
    }
    private static ScheduledTaskChange HandlerPlan() => new("test", "diagnostics", "handler", "test", Empty);
    private static ScheduledTaskChange ToChange(ScheduledTaskView p) => new(p.Name, p.PluginId, p.TargetType, p.TargetId, p.Arguments,
        p.ScheduleType, p.IntervalMinutes, p.CronExpression, p.TimeZoneId, p.IsEnabled, p.TimeoutSeconds, p.MaxRetries, p.RiskConfirmed);
    private static McpWebApplicationFactory Fixture(HandlerState state, TestClock? clock = null, string[]? pluginIds = null, bool addCronTemplate = false) => new(services =>
    {
        services.AddSingleton<TimeProvider>(clock ?? new TestClock());
        services.AddSingleton(state); services.AddTransient<TestHandler>();
        var runtime = (PluginRuntime)services.Last(d => d.ServiceType == typeof(PluginRuntime)).ImplementationInstance!;
        foreach (var plugin in runtime.Plugins.Where(p => p.Registration is not null))
            plugin.Registration!.Tasks.Add(new("test", "测试处理器", typeof(TestHandler),
                JsonSerializer.SerializeToElement(new { type = "object", properties = new { message = new { type = "string" } }, additionalProperties = false }), Idempotent: true));
        if (addCronTemplate) runtime.Plugins.Single(p => p.Manifest.Id == "diagnostics").Registration!.DefaultTasks.Add(
            new("cron", "hourly", "test", Empty, 60, ScheduleType: "cron", CronExpression: "0 * * * *"));
    }, pluginIds: pluginIds ?? ["diagnostics"]);
    private static IDbContextFactory<KaungMcpDbContext> Database(McpWebApplicationFactory f) => f.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>();
    private static SchedulerService Restart(McpWebApplicationFactory f) => new(Database(f), f.Services, f.Services.GetRequiredService<PluginRuntime>(),
        f.Services.GetRequiredService<TimeProvider>(), f.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), f.Services.GetRequiredService<ILogger<SchedulerService>>());
    private static HttpClient Admin(McpWebApplicationFactory f) { var client = f.CreateClient(); client.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey); return client; }
    private static async Task<TaskRunView> WaitTerminal(SchedulerService scheduler, Guid id)
    {
        for (var i = 0; i < 500; i++)
        { var run = (await scheduler.RunDetailAsync(id, default))!; if (run.Status is not ("queued" or "running")) return run; await Task.Delay(10); }
        throw new TimeoutException("Run did not complete.");
    }
    private static async Task<TaskRunView> WaitAttempt(SchedulerService scheduler, Guid id, int attempt)
    {
        for (var i = 0; i < 500; i++)
        { var run = (await scheduler.RunDetailAsync(id, default))!; if (run.Attempt == attempt && run.Status == "queued" && run.RetryAtUtc is not null) return run; await Task.Delay(10); }
        throw new TimeoutException("Retry was not persisted.");
    }
    public sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Origin { get; } = DateTimeOffset.Parse("2026-10-10T00:00:00Z");
        private TimeSpan elapsed;
        public override DateTimeOffset GetUtcNow() => Origin + elapsed;
        public void Advance(TimeSpan amount) => elapsed += amount;
    }
    public sealed class HandlerState
    {
        public int Calls;
        public int Failures;
        public bool Block;
        public bool IgnoreCancellation;
        public bool SharedKey;
        public bool Throw;
        public bool ValidationThrow;
        public ConcurrentDictionary<string, int> CallsByPlugin { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class TestHandler(HandlerState state) : IPluginTaskHandler
    {
        public Task ValidateAsync(JsonElement arguments, TaskValidationContext context, CancellationToken ct) => state.ValidationThrow ? throw new InvalidOperationException("validation fixture") : Task.CompletedTask;
        public IReadOnlyList<string> GetConcurrencyKeys(JsonElement arguments) => state.SharedKey ? ["shared/resource"] : [];
        public async Task<PluginTaskResult> ExecuteAsync(JsonElement arguments, PluginTaskExecutionContext context, CancellationToken ct)
        {
            if (arguments.TryGetProperty("message", out var message)) state.CallsByPlugin.AddOrUpdate(message.GetString()!, 1, (_, value) => value + 1);
            var call = Interlocked.Increment(ref state.Calls); state.Started.TrySetResult();
            if (state.Block) { if (state.IgnoreCancellation) await state.Release.Task; else await state.Release.Task.WaitAsync(ct); }
            if (state.Throw) throw new InvalidOperationException("fixture exception");
            return new(call > state.Failures, "test result", Error: call <= state.Failures ? "transient fixture" : null);
        }
    }
}
