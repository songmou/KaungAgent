using System.Net.Http.Json;
using System.Runtime.Loader;
using System.Text.Json;
using KaungMcp.Clients;
using KaungMcp.Configuration;
using KaungMcp.Data;
using KaungMcp.Plugins;
using KaungMcp.Scheduling;
using KaungMcp.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace KaungMcp.Tests;

public sealed class PluginRuntimeTests
{
    [Fact]
    public async Task Independently_built_plugin_loads_tools_resources_tasks_and_runtime_configuration()
    {
        await using var package = new PluginFixture();
        package.InstallSample();
        await using var factory = package.Host();
        using var admin = Admin(factory);
        var runtime = factory.Services.GetRequiredService<PluginRuntime>();
        var loaded = Assert.Single(runtime.Plugins);
        Assert.Equal("ready", loaded.Status);
        Assert.NotEqual(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(loaded.Instance!.GetType().Assembly));
        Assert.Equal("sample", runtime.CapabilityOwner("resources/read", "kaung://sample/items/42"));
        Assert.Null(runtime.CapabilityOwner("resources/read", "kaung://sample/items/42/unknown"));
        Assert.DoesNotContain(loaded.Instance.GetType().Assembly.GetReferencedAssemblies(), a => a.Name == "KaungMcp");

        var created = await admin.PostAsJsonAsync("/api/admin/clients", new CreateClientRequest("sample", null));
        var key = (await created.Content.ReadFromJsonAsync<CreatedClientResponse>())!.ApiKey;
        using var http = factory.CreateClient();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new()
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + key }
        }, http, ownsHttpClient: false));
        Assert.Single(await mcp.ListToolsAsync());
        var item = await mcp.ReadResourceAsync("kaung://sample/items/42");
        Assert.Contains("Item: 42", JsonSerializer.Serialize(item));
        var config = await admin.PutAsJsonAsync("/api/admin/settings", new { changes = new Dictionary<string, string> { ["Sample:Prefix"] = "Updated: " } });
        config.EnsureSuccessStatusCode();
        var echo = await mcp.CallToolAsync("kaung_sample_echo", new Dictionary<string, object?> { ["message"] = "hello" });
        Assert.Contains("Updated:hello", JsonSerializer.Serialize(echo));

        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var plan = Assert.Single((await scheduler.ListAsync(1, 20, null, null, default)).Items);
        Assert.False(plan.IsEnabled);
        var run = await scheduler.SubmitAsync(plan.Id, "manual", default);
        await scheduler.DispatchAsync(default);
        var result = await WaitTerminal(scheduler, run);
        Assert.Equal("succeeded", result.Status);
        Assert.NotNull(result.InvocationLogId);
        Assert.Equal("示例报告内容", await File.ReadAllTextAsync(Path.Combine(package.Data, "sample", "artifacts", result.Artifact!.Id)));

        await runtime.SetEnabledAsync("sample", false, factory.Services, default);
        Assert.Empty(await mcp.ListToolsAsync());
        await Assert.ThrowsAnyAsync<McpException>(() => mcp.ReadResourceAsync("kaung://sample/items/42").AsTask());
    }

    [Fact]
    public async Task Uninstall_reinstall_and_upgrade_preserve_plans_data_config_and_template_tombstones()
    {
        await using var package = new PluginFixture();
        package.InstallSample();
        Guid id;
        await using (var host = package.Host())
        {
            using var admin = Admin(host);
            var scheduler = host.Services.GetRequiredService<SchedulerService>();
            var template = Assert.Single((await scheduler.ListAsync(1, 20, null, null, default)).Items);
            await scheduler.SetStateAsync(template.Id, "delete", default);
            var plan = await scheduler.CreateAsync(new("user plan", "sample", "handler", "write_report", JsonSerializer.SerializeToElement(new { text = "retained" })), default);
            id = plan.Id;
            var runId = await scheduler.SubmitAsync(id, "manual", default);
            await scheduler.DispatchAsync(default);
            Assert.Equal("succeeded", (await WaitTerminal(scheduler, runId)).Status);
            (await admin.PutAsJsonAsync("/api/admin/settings", new { changes = new Dictionary<string, string> { ["Sample:Prefix"] = "Saved" } })).EnsureSuccessStatusCode();
        }
        Directory.Delete(Path.Combine(package.Code, "sample"), true);
        await using (var host = package.Host())
        {
            using var admin = Admin(host);
            var scheduler = host.Services.GetRequiredService<SchedulerService>();
            Assert.NotNull((await scheduler.GetAsync(id, default))!.BlockedReason);
            Assert.Single((await scheduler.RunsAsync(id, null, 1, 20, default)).Items);
            var plugins = await admin.GetFromJsonAsync<JsonElement[]>("/api/admin/plugins");
            Assert.Equal("missing", Assert.Single(plugins!).GetProperty("status").GetString());
            Assert.Single(Directory.GetFiles(Path.Combine(package.Data, "sample", "artifacts")));
        }
        package.InstallSample("2.0.0");
        await using (var host = package.Host())
        {
            using var admin = Admin(host);
            var scheduler = host.Services.GetRequiredService<SchedulerService>();
            var onlyPlan = Assert.Single((await scheduler.ListAsync(1, 20, null, null, default)).Items);
            Assert.Equal(id, onlyPlan.Id);
            Assert.Null(onlyPlan.BlockedReason);
            var config = JsonSerializer.Serialize(host.Services.GetRequiredService<ManagedSettingsStore>().Snapshot());
            Assert.Contains("Saved", config);
            var restored = await admin.PutAsJsonAsync("/api/admin/settings", new { clear = new[] { "Sample:Prefix" } });
            restored.EnsureSuccessStatusCode();
            Assert.Equal("Sample: ", (await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("effective").GetProperty("Sample:Prefix").GetString());
            var db = await host.Services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>().CreateDbContextAsync();
            await using (db) Assert.True(await db.ScheduledTasks.AnyAsync(p => p.TemplateId == "report" && p.IsDeleted));
        }
    }

    [Fact]
    public async Task Invalid_manifest_conflicting_capabilities_and_failed_initialization_are_isolated()
    {
        await using var package = new PluginFixture();
        package.InstallSample();
        Directory.CreateSymbolicLink(Path.Combine(package.Code, "diagnostics"), Path.Combine(AppContext.BaseDirectory, "plugins", "diagnostics"));
        Directory.CreateDirectory(Path.Combine(package.Code, "invalid"));
        await File.WriteAllTextAsync(Path.Combine(package.Code, "invalid", "plugin.json"), "invalid json");
        package.InstallSample(id: "conflict");
        await using var host = package.Host(c => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Sample:Prefix"] = new string('x', 101) }));
        using var admin = Admin(host);
        var runtime = host.Services.GetRequiredService<PluginRuntime>();
        Assert.True(runtime.Available("diagnostics"));
        Assert.Equal(3, runtime.Plugins.Count(p => p.Status == "failed"));
        Assert.NotNull(runtime.Plugins.Single(p => p.Manifest.Id == "invalid").Error);
        var tools = await admin.GetFromJsonAsync<JsonElement>("/api/admin/tools");
        Assert.Contains("kaung_ping", tools.GetRawText());
    }

    [Fact]
    public async Task Plugin_disabled_before_restart_requires_restart_after_reenabling()
    {
        await using var package = new PluginFixture(); package.InstallSample();
        await using (var host = package.Host())
        {
            using var client = host.CreateClient();
            await host.Services.GetRequiredService<PluginRuntime>().SetEnabledAsync("sample", false, host.Services, default);
        }
        await using (var host = package.Host())
        {
            using var client = host.CreateClient();
            var runtime = host.Services.GetRequiredService<PluginRuntime>();
            Assert.Equal("disabled", Assert.Single(runtime.Plugins).Status);
            await runtime.SetEnabledAsync("sample", true, host.Services, default);
            Assert.False(runtime.Available("sample"));
        }
        await using (var host = package.Host())
        {
            using var client = host.CreateClient();
            Assert.True(host.Services.GetRequiredService<PluginRuntime>().Available("sample"));
        }
    }

    [Fact]
    public async Task Missing_handler_dependencies_fail_only_the_owning_plugin()
    {
        await using var package = new PluginFixture(); package.InstallSample();
        Directory.CreateSymbolicLink(Path.Combine(package.Code, "diagnostics"), Path.Combine(AppContext.BaseDirectory, "plugins", "diagnostics"));
        await using var host = package.Host(configureServices: services =>
        {
            var runtime = (PluginRuntime)services.Last(d => d.ServiceType == typeof(PluginRuntime)).ImplementationInstance!;
            services.RemoveAll(runtime.TaskDefinition("sample", "write_report")!.HandlerType);
        });
        using var admin = Admin(host);
        var runtime = host.Services.GetRequiredService<PluginRuntime>();
        Assert.True(runtime.Available("diagnostics"));
        var broken = runtime.Plugins.Single(p => p.Manifest.Id == "sample");
        Assert.Equal("failed", broken.Status); Assert.NotNull(broken.Error);
        Assert.Empty((await host.Services.GetRequiredService<SchedulerService>().ListAsync(1, 20, "sample", null, default)).Items);
    }

    [Fact]
    public void Unsupported_schema_constraints_are_explicitly_rejected()
    {
        Assert.Throws<ArgumentException>(() => ArgumentSchema.CheckSupported(JsonSerializer.SerializeToElement(new { type = "object", patternProperties = new { } })));
        var schema = JsonSerializer.SerializeToElement(new { anyOf = new[] { new { type = "string" }, new { type = "integer" } }, minLength = 3 });
        Assert.Throws<ArgumentException>(() => ArgumentSchema.Validate(JsonSerializer.SerializeToElement("a"), schema));
    }

    private static HttpClient Admin(McpWebApplicationFactory host)
    {
        var client = host.CreateClient(); client.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey); return client;
    }
    private static async Task<TaskRunView> WaitTerminal(SchedulerService scheduler, Guid id)
    {
        for (var i = 0; i < 500; i++)
        {
            var run = (await scheduler.RunDetailAsync(id, default))!;
            if (run.Status is not ("running" or "queued")) return run;
            await Task.Delay(10);
        }
        throw new TimeoutException("Plugin task did not finish.");
    }
    private sealed class PluginFixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kaung-plugin-" + Guid.NewGuid().ToString("N"));
        public string Code => Path.Combine(root, "code");
        public string Data => Path.Combine(root, "host.db.plugins");
        public PluginFixture() => Directory.CreateDirectory(Code);
        public void InstallSample(string version = "1.0.0", string id = "sample")
        {
            var source = Path.Combine(AppContext.BaseDirectory, "plugin-fixtures", "sample");
            var target = Path.Combine(Code, id);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var output = Path.Combine(target, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.Copy(file, output, true);
            }
            var path = Path.Combine(target, "plugin.json");
            var manifest = JsonSerializer.Deserialize<KaungMcp.Plugin.PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            File.WriteAllText(path, JsonSerializer.Serialize(manifest with { Id = id, Version = version }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        public McpWebApplicationFactory Host(Action<IConfigurationBuilder>? configureConfiguration = null, Action<IServiceCollection>? configureServices = null) => new(configureTestServices: configureServices, databasePath: Path.Combine(root, "host.db"),
            codeDirectory: Code, preserveData: true, shareLoadedAssemblies: false, configureConfiguration: configureConfiguration);
        public ValueTask DisposeAsync() { Directory.Delete(root, true); return ValueTask.CompletedTask; }
    }
}
