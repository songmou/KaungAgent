using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KaungMcp.Data;
using KaungMcp.InvocationLogs;
using KaungMcp.Jewelry;
using KaungMcp.Scheduling;
using KaungMcp.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KaungMcp.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public void Schedule_validation_checks_time_zone_cron_and_five_occurrences()
    {
        var now = new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero);
        var interval = SchedulerService.ValidateSchedule(new("interval", 15, null, "Asia/Shanghai"), now);
        Assert.Equal(5, interval.NextOccurrencesUtc.Length);
        Assert.Equal(now.AddMinutes(15), interval.NextOccurrencesUtc[0]);
        var cron = SchedulerService.ValidateSchedule(new("cron", null, "0 9 * * *", "Asia/Shanghai"), now);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 1, 0, 0, TimeSpan.Zero), cron.NextOccurrencesUtc[0]);
        Assert.Equal(cron.NextOccurrencesUtc[0].AddDays(1), cron.NextOccurrencesUtc[1]);
        Assert.Throws<ArgumentException>(() => SchedulerService.ValidateSchedule(new("cron", null, "bad", "UTC"), now));
        Assert.Throws<ArgumentException>(() => SchedulerService.ValidateSchedule(new("interval", 0, null, "UTC"), now));
        Assert.Throws<ArgumentException>(() => SchedulerService.ValidateSchedule(new("interval", 15, null, "Bad/Zone"), now));
    }

    [Fact]
    public async Task Seeds_are_idempotent_and_restart_skips_missed_schedules()
    {
        await using var factory = new McpWebApplicationFactory();
        using var client = factory.CreateClient();
        var service = factory.Services.GetRequiredService<SchedulerService>();
        await service.InitializeAsync(default);
        await service.InitializeAsync(default);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>()
            .CreateDbContextAsync();
        var tasks = await db.ScheduledTasks.AsNoTracking().ToListAsync();
        Assert.Equal(3, tasks.Count);
        Assert.Equal(15, tasks.Single(x => x.Name == "国际金价采集").IntervalMinutes);
        Assert.Equal(15, tasks.Single(x => x.Name == "上海金价采集").IntervalMinutes);
        Assert.Equal(60, tasks.Single(x => x.Name == "品牌金价采集").IntervalMinutes);
        Assert.All(tasks, x => Assert.True(x.IsEnabled));
        Assert.Empty(await db.ScheduledTaskRuns.ToListAsync());
    }

    [Fact]
    public async Task Restart_interrupts_orphaned_run_without_replaying_missed_ticks()
    {
        await using var factory = new McpWebApplicationFactory();
        using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        await scheduler.InitializeAsync(default);
        var dbFactory = factory.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>();
        Guid id;
        long runId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var task = await db.ScheduledTasks.FirstAsync();
            id = task.Id;
            task.Name = "用户改过的任务名";
            task.NextRunTicks = DateTimeOffset.UtcNow.AddDays(-2).UtcTicks;
            var run = new ScheduledTaskRun { TaskId = id, StartedTicks = DateTimeOffset.UtcNow.AddHours(-1).UtcTicks };
            db.ScheduledTaskRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }
        var restarted = new SchedulerService(dbFactory,
            factory.Services, factory.Services.GetRequiredService<IOptions<JewelryOptions>>(),
            factory.Services.GetRequiredService<TimeProvider>(),
            factory.Services.GetRequiredService<ILogger<SchedulerService>>());
        await restarted.InitializeAsync(default);
        await using var check = await dbFactory.CreateDbContextAsync();
        Assert.True((await check.ScheduledTasks.SingleAsync(x => x.Id == id)).NextRunTicks > DateTimeOffset.UtcNow.UtcTicks);
        Assert.Equal("interrupted", (await check.ScheduledTaskRuns.SingleAsync(x => x.Id == runId)).Status);
        Assert.Single(await check.ScheduledTaskRuns.ToListAsync());
        Assert.Equal(3, await check.ScheduledTasks.CountAsync());
    }

    [Fact]
    public async Task Same_task_overlap_is_skipped_and_query_catchup_obeys_source_backoff()
    {
        var adapter = new BlockingAdapter();
        await using var factory = new McpWebApplicationFactory(services => services.AddSingleton<IJewelryAdapter>(adapter));
        using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var task = (await scheduler.ListAsync(1, 20, "active", null, null, default)).Items
            .Single(x => x.Name == "国际金价采集");
        var running = scheduler.RunAsync(task.Id, "manual", true, default);
        await adapter.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var skipped = await scheduler.RunAsync(task.Id, "manual", true, default);
        Assert.Equal("skipped", skipped!.Status);
        adapter.Release.SetResult();
        var finished = await running;
        Assert.Equal("failed", finished!.Status);
        await scheduler.CollectOnQueryAsync(["gold_api"], default);
        Assert.Equal(1, adapter.Calls);
    }

    [Fact]
    public async Task Scheduler_tick_executes_each_seeded_group_once()
    {
        var adapter = new EmptyAdapter();
        await using var factory = new McpWebApplicationFactory(services => services.AddSingleton<IJewelryAdapter>(adapter));
        using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        await scheduler.TickAsync(default);
        Assert.Equal(5, adapter.Calls);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>()
            .CreateDbContextAsync();
        Assert.Equal(3, await db.ScheduledTaskRuns.CountAsync());
    }

    [Fact]
    public async Task Admin_can_manage_tool_schedule_and_inspect_scheduler_invocation_log()
    {
        await using var factory = new McpWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.GetAsync("/api/admin/scheduled-tasks")).StatusCode);
        Assert.Contains("定时任务", await (await anonymous.GetAsync("/admin/")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/admin/scheduler.js")).StatusCode);
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var createdResponse = await admin.PostAsJsonAsync("/api/admin/scheduled-tasks", new
        {
            name = "Echo hourly", description = "test", executionType = "mcp_tool", target = "kaung_echo",
            arguments = new { message = "scheduled hello" }, scheduleType = "interval", intervalMinutes = 60,
            timeZoneId = "Asia/Shanghai", isEnabled = true
        });
        Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);
        var task = await createdResponse.Content.ReadFromJsonAsync<ScheduledTaskView>();
        Assert.NotNull(task);
        var runResponse = await admin.PostAsync($"/api/admin/scheduled-tasks/{task.Id}/run", null);
        runResponse.EnsureSuccessStatusCode();
        var run = await runResponse.Content.ReadFromJsonAsync<ScheduledRunView>();
        Assert.NotNull(run);
        Assert.Equal("succeeded", run.Status);
        Assert.NotNull(run.InvocationLogId);
        var log = await admin.GetFromJsonAsync<InvocationLogDetailResponse>(
            $"/api/admin/call-logs/{run.InvocationLogId}");
        Assert.NotNull(log);
        Assert.Equal(InvocationSources.Scheduler, log.Log.Source);
        Assert.Contains("scheduled hello", log.RequestParametersJson);
        var runs = await admin.GetFromJsonAsync<ScheduledRunPage>(
            $"/api/admin/scheduled-tasks/{task.Id}/runs");
        Assert.Single(runs!.Items);
        (await admin.PostAsync($"/api/admin/scheduled-tasks/{task.Id}/disable", null)).EnsureSuccessStatusCode();
        (await admin.DeleteAsync($"/api/admin/scheduled-tasks/{task.Id}")).EnsureSuccessStatusCode();
        Assert.True((await admin.GetFromJsonAsync<ScheduledTaskView>($"/api/admin/scheduled-tasks/{task.Id}"))!.IsArchived);
    }

    [Fact]
    public async Task Changed_tool_risk_blocks_automatic_execution_until_reconfirmed()
    {
        await using var factory = new McpWebApplicationFactory();
        using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var task = await scheduler.CreateAsync(new ScheduledTaskChange("Risk drift", null, "mcp_tool", "kaung_echo",
            JsonSerializer.SerializeToElement(new { message = "safe" }), "interval", 60, null,
            "Asia/Shanghai", true), default);
        var tool = factory.Services.GetServices<McpServerTool>()
            .Single(x => x.ProtocolTool.Name == "kaung_echo");
        tool.ProtocolTool.Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true };
        var blocked = await scheduler.RunAsync(task.Id, "manual", true, default);
        Assert.Equal("blocked", blocked!.Status);
        Assert.Null(blocked.InvocationLogId);
    }

    [Fact]
    public async Task Gold_sources_reject_duplicate_enabled_owner_and_too_fast_cron()
    {
        await using var factory = new McpWebApplicationFactory();
        using var client = factory.CreateClient();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var gold = JsonSerializer.SerializeToElement(new { sourceIds = new[] { "gold_api" } });
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(new ScheduledTaskChange(
            "duplicate", null, "internal_handler", "jewelry.collect_sources", gold,
            "interval", 15, null, "Asia/Shanghai", true), default));
        var brand = JsonSerializer.SerializeToElement(new { sourceIds = new[] { "lukfook" } });
        await Assert.ThrowsAsync<ArgumentException>(() => scheduler.CreateAsync(new ScheduledTaskChange(
            "too fast", null, "internal_handler", "jewelry.collect_sources", brand,
            "cron", null, "* * * * *", "Asia/Shanghai", false), default));
    }

    private sealed class BlockingAdapter : IJewelryAdapter
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<IReadOnlyList<JewelryQuote>> CollectAsync(JewelrySourceDefinition source, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            throw new HttpRequestException("Simulated source outage.");
        }
    }

    private sealed class EmptyAdapter : IJewelryAdapter
    {
        public int Calls;
        public Task<IReadOnlyList<JewelryQuote>> CollectAsync(JewelrySourceDefinition source, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<JewelryQuote>>([]);
        }
    }
}
