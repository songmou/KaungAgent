using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KaungMcp.Capabilities;
using KaungMcp.Configuration;
using KaungMcp.Data;
using KaungMcp.Data.Entities;
using KaungMcp.InvocationLogs;
using KaungMcp.Plugin;
using KaungMcp.Plugins;
using KaungMcp.Scheduling;
using KaungMcp.Security;
using KaungMcp.Tools;
using KaungMcp.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace KaungMcp.Tests;

public sealed class WebModuleManagementTests
{
    private static McpWebApplicationFactory Host(bool search = true, bool fetch = true) => new(services =>
    {
        services.RemoveAll<IWebSearchProvider>(); services.AddSingleton<IWebSearchProvider, FakeSearch>();
    }, pluginIds: [], configureConfiguration: c => c.AddInMemoryCollection(new Dictionary<string, string?>
    { ["WebTools:SearchEnabled"] = search.ToString(), ["WebTools:FetchEnabled"] = fetch.ToString() }));
    private static HttpClient Admin(McpWebApplicationFactory host)
    { var client = host.CreateClient(); client.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey); return client; }
    private static Task<KaungMcpDbContext> Database(McpWebApplicationFactory host) => host.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>().CreateDbContextAsync();
    private static ScheduledTaskChange Plan(string name = "网页巡检") => new(name, "web", "tool", "kaung_web_search", JsonSerializer.SerializeToElement(new { query = "fixture" }));

    [Fact]
    public async Task Built_in_web_needs_no_plugin_package_and_configuration_has_one_owner()
    {
        await using var host = Host(); using var admin = Admin(host); using var anonymous = host.CreateClient();
        Assert.Empty(await admin.GetFromJsonAsync<JsonElement[]>("/api/admin/plugins") ?? []);
        Assert.Empty(host.Services.GetRequiredService<PluginRuntime>().Plugins);
        var module = await admin.GetFromJsonAsync<JsonElement>("/api/admin/modules/web");
        Assert.Equal("module", module.GetProperty("ownerKind").GetString()); Assert.Equal(2, module.GetProperty("tools").GetArrayLength());
        foreach (var path in new[] { "/modules/web", "/capability-owners", "/settings/definitions" }) Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin" + path)).StatusCode);
        var definitions = (await admin.GetFromJsonAsync<JsonElement[]>("/api/admin/settings/definitions"))!;
        Assert.Equal(definitions.Length, definitions.Select(x => x.GetProperty("definition").GetProperty("key").GetString()).Distinct().Count());
        Assert.Equal("host", definitions.Single(x => x.GetProperty("definition").GetProperty("key").GetString() == "WebFetch:TimeoutSeconds").GetProperty("ownerId").GetString());
        Assert.Equal("web", definitions.Single(x => x.GetProperty("definition").GetProperty("key").GetString() == "WebFetch:BrowserFallbackEnabled").GetProperty("ownerId").GetString());
        var old = new PluginSettingDefinition("ThirdParty:Text", "文本", "default");
        Assert.Equal("text", old.Type); Assert.Equal("general", old.GroupId);
        var tested = await admin.PostAsJsonAsync("/api/admin/tools/kaung_web_search/test", new { arguments = new { query = "fixture" } });
        tested.EnsureSuccessStatusCode(); Assert.True((await tested.Content.ReadFromJsonAsync<TestMcpToolResponse>())!.Succeeded);
    }
    [Fact]
    public async Task Saved_tool_switches_override_defaults_and_block_queued_plans_immediately()
    {
        await using var host = Host(); using var admin = Admin(host);
        var scheduler = host.Services.GetRequiredService<SchedulerService>(); var plan = await scheduler.CreateAsync(Plan(), default);
        var fetchPlan = await scheduler.CreateAsync(Plan("读取计划") with { TargetId = "kaung_web_fetch", Arguments = JsonSerializer.SerializeToElement(new { url = "https://example.test" }) }, default);
        var queued = await scheduler.SubmitAsync(plan.Id, "manual", default);
        (await admin.PutAsJsonAsync("/api/admin/tools/kaung_web_search/enabled", new { isEnabled = false })).EnsureSuccessStatusCode();
        Assert.NotNull((await scheduler.GetAsync(plan.Id, default))!.BlockedReason);
        Assert.Equal(fetchPlan.NextRunAtUtc, (await scheduler.GetAsync(fetchPlan.Id, default))!.NextRunAtUtc);
        Assert.Equal("cancelled", (await scheduler.RunDetailAsync(queued, default))!.Status);
        Assert.False(await host.Services.GetRequiredService<McpToolManagementService>().IsEnabledAsync("kaung_web_search", default));
        (await admin.PutAsJsonAsync("/api/admin/tools/kaung_web_search/enabled", new { isEnabled = true })).EnsureSuccessStatusCode();
        Assert.Null((await scheduler.GetAsync(plan.Id, default))!.BlockedReason);
        await using var defaultsOff = Host(false, false); using var otherAdmin = Admin(defaultsOff);
        Assert.False(await defaultsOff.Services.GetRequiredService<McpToolManagementService>().IsEnabledAsync("kaung_web_fetch", default));
        (await otherAdmin.PutAsJsonAsync("/api/admin/tools/kaung_web_fetch/enabled", new { isEnabled = true })).EnsureSuccessStatusCode();
        Assert.True(await defaultsOff.Services.GetRequiredService<McpToolManagementService>().IsEnabledAsync("kaung_web_fetch", default));
    }
    [Fact]
    public async Task Legacy_disabled_web_is_migrated_once_without_overwriting_tool_settings()
    {
        await using var host = Host(); using var admin = Admin(host); await using var db = await Database(host);
        db.PluginStates.Add(new() { Id = "web", Name = "旧网页插件", Version = "1.0.0", IsEnabled = false });
        db.ToolSettings.Add(new() { ToolName = "kaung_web_search", IsEnabled = true, UpdatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        var registry = host.Services.GetRequiredService<CapabilityRegistry>(); await registry.InitializeAsync(db, default);
        Assert.Null(await db.PluginStates.FindAsync("web")); Assert.True((await db.ToolSettings.FindAsync("kaung_web_search"))!.IsEnabled);
        var fetch = (await db.ToolSettings.FindAsync("kaung_web_fetch"))!; Assert.False(fetch.IsEnabled);
        fetch.IsEnabled = true; await db.SaveChangesAsync(); await registry.InitializeAsync(db, default);
        Assert.True((await db.ToolSettings.FindAsync("kaung_web_fetch"))!.IsEnabled);
        var folder = Path.Combine(Path.GetTempPath(), "kaung-web-legacy-" + Guid.NewGuid()); Directory.CreateDirectory(Path.Combine(folder, "web"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "web", "plugin.json"), "invalid legacy package");
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["Plugins:Directory"] = folder }).Build();
            var runtime = PluginRuntime.Discover(new ServiceCollection(), configuration); Assert.Empty(runtime.Plugins); Assert.True(runtime.LegacyWebPackagePresent);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact]
    public async Task Owner_alias_runs_and_logs_filter_before_pagination_and_link_both_directions()
    {
        await using var host = Host(); using var admin = Admin(host);
        var response = await admin.PostAsJsonAsync("/api/admin/scheduled-tasks", new { name = "网页巡检", ownerId = "web", targetType = "tool", targetId = "kaung_web_search", arguments = new { query = "fixture" } });
        response.EnsureSuccessStatusCode(); var plan = (await response.Content.ReadFromJsonAsync<ScheduledTaskView>())!;
        var invalid = await admin.PostAsJsonAsync("/api/admin/scheduled-tasks", new { name = "conflict", ownerId = "web", pluginId = "diagnostics", targetType = "tool", targetId = "kaung_web_search", arguments = new { query = "fixture" } });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var scheduler = host.Services.GetRequiredService<SchedulerService>(); var id = await scheduler.SubmitAsync(plan.Id, "manual", default); await scheduler.DispatchAsync(default);
        TaskRunView? run = null;
        for (var attempt = 0; attempt < 100; attempt++) { run = await scheduler.RunDetailAsync(id, default); if (run!.Status == "succeeded") break; await Task.Delay(20); }
        Assert.Equal("succeeded", run!.Status); Assert.Equal("网页巡检", run.TaskName); Assert.Equal("module", run.OwnerKind); Assert.NotNull(run.InvocationLogId);
        var page = await admin.GetFromJsonAsync<SchedulerPage<TaskRunView>>("/api/admin/task-runs?ownerId=web&ownerKind=module&status=succeeded&trigger=manual&pageSize=1");
        Assert.Equal(1, page!.TotalCount); Assert.Equal(id, Assert.Single(page.Items).Id);
        Assert.Equal(0, (await admin.GetFromJsonAsync<SchedulerPage<TaskRunView>>("/api/admin/task-runs?ownerKind=plugin"))!.TotalCount);
        var log = (await admin.GetFromJsonAsync<InvocationLogDetailResponse>($"/api/admin/call-logs/{run.InvocationLogId}"))!.Log;
        Assert.Equal("web", log.OwnerId); Assert.Equal("module", log.OwnerKind); Assert.Equal(id, log.TaskRunId);
        var logs = await admin.GetFromJsonAsync<KaungMcp.Clients.PagedResponse<InvocationLogResponse>>($"/api/admin/call-logs?ownerId=web&correlationId={run.CorrelationId}&pageSize=1");
        Assert.Equal(1, logs!.TotalCount); Assert.Equal(log.Id, Assert.Single(logs.Items).Id);
        Assert.Equal(0, (await admin.GetFromJsonAsync<KaungMcp.Clients.PagedResponse<InvocationLogResponse>>("/api/admin/call-logs?ownerKind=plugin"))!.TotalCount);
    }
    [Fact]
    public async Task Backfill_recognizes_known_targets_but_leaves_unknown_targets_unassigned()
    {
        await using var host = Host(); using var admin = Admin(host); await using var db = await Database(host);
        var now = DateTimeOffset.UtcNow;
        db.InvocationLogs.AddRange(new McpInvocationLog { RequestMethod = "tools/call", Source = "admin_test", TargetName = "kaung_web_search", StartedAtUtc = now },
            new McpInvocationLog { RequestMethod = "tools/call", Source = "admin_test", TargetName = "invented/unknown", StartedAtUtc = now },
            new McpInvocationLog { RequestMethod = "tools/call", Source = "admin_test", TargetName = "invented/kaung_web_search", StartedAtUtc = now }); await db.SaveChangesAsync();
        await host.Services.GetRequiredService<CapabilityRegistry>().InitializeAsync(db, default);
        var logs = await db.InvocationLogs.OrderBy(l => l.Id).ToListAsync(); Assert.Equal("web", logs[0].OwnerId); Assert.Null(logs[1].OwnerId); Assert.Null(logs[1].OwnerKind); Assert.Null(logs[2].OwnerId);
    }
    private sealed class FakeSearch : IWebSearchProvider
    {
        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken ct) => Task.FromResult(new WebSearchResponse(request.Query, "fixture", 1, 0, [], [], []));
        public Task<bool> IsHealthyAsync(CancellationToken ct) => Task.FromResult(true);
    }
}
