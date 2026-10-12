using System.Reflection;
using System.Collections.Concurrent;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using KaungMcp.Configuration;
using KaungMcp.Data;
using KaungMcp.Plugin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;

namespace KaungMcp.Plugins;

public sealed class PluginState
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
}
public sealed class LoadedPlugin(PluginManifest manifest, string directory, string dataDirectory)
{
    public PluginManifest Manifest { get; } = manifest;
    public string Directory { get; } = directory;
    public string DataDirectory { get; } = dataDirectory;
    public bool IsEnabled { get; set; } = true;
    public string Status { get; set; } = "discovered";
    public string? Error { get; set; }
    public IKaungMcpPlugin? Instance { get; set; }
    public PluginRegistrationContext? Registration { get; set; }
    public AssemblyLoadContext? LoadContext { get; set; }
}
public sealed class PluginRuntime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, LoadedPlugin> plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string PluginId, PluginToolPolicy Policy)> tools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> prompts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> resources = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Time, IReadOnlyList<PluginHealth> Health)> healthCache = new();
    public IEnumerable<LoadedPlugin> Plugins => plugins.Values;
    public bool LegacyWebPackagePresent { get; private set; }
    public string? CapabilityOwner(string method, string target) => method switch
    {
        "prompts/get" => prompts.GetValueOrDefault(target),
        "resources/read" => resources.GetValueOrDefault(target) ?? resources.FirstOrDefault(r =>
            r.Key.Contains('{') && Regex.IsMatch(target, ResourcePattern(r.Key), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))).Value,
        _ => null
    };
    private static string ResourcePattern(string template) => "^" + string.Concat(Regex.Split(template, "(\\{[^}]+\\})")
        .Select(part => part.StartsWith('{') && part.EndsWith('}') ? "[^/?#]+" : Regex.Escape(part))) + "$";
    public string? ToolOwner(string name) => tools.TryGetValue(name, out var entry) ? entry.PluginId : null;
    public PluginToolPolicy? ToolPolicy(string name) => tools.TryGetValue(name, out var entry) ? entry.Policy : null;
    public bool Available(string? id) => id is not null && plugins.TryGetValue(id, out var plugin) && plugin.IsEnabled && plugin.Status == "ready";
    public bool PromptAvailable(string name) => prompts.TryGetValue(name, out var id) && Available(id);
    public bool ResourceAvailable(string uri) => resources.TryGetValue(uri, out var id) && Available(id);
    public PluginTaskDefinition? TaskDefinition(string pluginId, string target) => plugins.GetValueOrDefault(pluginId)?.Registration?.Tasks.SingleOrDefault(t => t.Id == target);

    public static PluginRuntime Discover(IServiceCollection services, IConfiguration configuration)
    {
        var runtime = new PluginRuntime();
        var directory = Path.GetFullPath(configuration["Plugins:Directory"] ?? "plugins", AppContext.BaseDirectory);
        var dataDirectory = Path.GetFullPath(configuration["Plugins:DataDirectory"] ?? "data/plugins");
        if (!System.IO.Directory.Exists(directory)) return runtime;
        foreach (var folder in System.IO.Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(folder) == "web") { runtime.LegacyWebPackagePresent = true; continue; }
            LoadedPlugin? plugin = null;
            try
            {
                var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(Path.Combine(folder, "plugin.json")), Json)
                    ?? throw new InvalidOperationException("插件清单为空。");
                if (!Regex.IsMatch(manifest.Id ?? "", "^[a-z][a-z0-9_-]{0,63}$") || manifest.Id != Path.GetFileName(folder))
                    throw new InvalidOperationException("插件 ID 必须与目录名一致。");
                if (manifest.Id == "host") throw new InvalidOperationException("host 是宿主保留的归属 ID。");
                if (runtime.plugins.ContainsKey(manifest.Id)) throw new InvalidOperationException("插件 ID 重复。");
                plugin = new(manifest, folder, Path.Combine(dataDirectory, manifest.Id));
                runtime.plugins.Add(manifest.Id, plugin);
                plugin.IsEnabled = ReadEnabled(configuration, manifest.Id);
                if (!plugin.IsEnabled) { plugin.Status = "disabled"; continue; }
                if (manifest.ContractVersion != 1 || !Version.TryParse(manifest.Version, out _))
                    throw new InvalidOperationException("插件版本或契约版本不兼容。");
                if (Path.GetFileName(manifest.EntryAssembly) != manifest.EntryAssembly || !manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("入口程序集必须位于插件目录。");
                var path = Path.Combine(folder, manifest.EntryAssembly);
                var load = new PluginLoadContext(path);
                plugin.LoadContext = load;
                // Tests can share their already-loaded business types to inject fixture adapters.
                var assembly = configuration.GetValue("Plugins:ShareLoadedAssemblies", false)
                    ? AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == Path.GetFileNameWithoutExtension(path)) ?? load.LoadFromAssemblyPath(path)
                    : load.LoadFromAssemblyPath(path);
                plugin.Instance = Activator.CreateInstance(assembly.GetType(manifest.EntryType, true)!) as IKaungMcpPlugin
                    ?? throw new InvalidOperationException("入口类型未实现插件契约。");
                var staged = new ServiceCollection();
                var registration = new PluginRegistrationContext(staged, configuration, plugin.DataDirectory);
                plugin.Instance.Register(registration);
                if (staged.Any(d => typeof(IHostedService).IsAssignableFrom(d.ServiceType)))
                    throw new InvalidOperationException("插件不得注册后台调度循环。");
                if (registration.Tasks.Select(t => t.Id).Distinct().Count() != registration.Tasks.Count ||
                    registration.DefaultTasks.Select(t => t.Id).Distinct().Count() != registration.DefaultTasks.Count)
                    throw new InvalidOperationException("任务或模板 ID 重复。");
                foreach (var task in registration.Tasks)
                {
                    ArgumentSchema.CheckSupported(task.ParameterSchema);
                    if (!typeof(IPluginTaskHandler).IsAssignableFrom(task.HandlerType) || string.IsNullOrWhiteSpace(task.Id) || task.Version < 1 || task.DefaultTimeoutSeconds is < 1 || task.MaxTimeoutSeconds > 86400 || task.MaxTimeoutSeconds < task.DefaultTimeoutSeconds)
                        throw new InvalidOperationException("任务处理器定义无效。");
                }
                foreach (var template in registration.DefaultTasks)
                {
                    if (string.IsNullOrWhiteSpace(template.Id) || template.Id.Length > 200 || string.IsNullOrWhiteSpace(template.Name)) throw new InvalidOperationException("默认计划标识或名称无效。");
                    var definition = registration.Tasks.SingleOrDefault(t => t.Id == template.HandlerId)
                        ?? throw new InvalidOperationException("默认计划引用了未知处理器。");
                    ArgumentSchema.Validate(template.Arguments, definition.ParameterSchema);
                    _ = KaungMcp.Scheduling.SchedulerService.Next(new(template.ScheduleType, template.IntervalMinutes, template.CronExpression, template.TimeZoneId), DateTimeOffset.UtcNow);
                }
                if (registration.Tools.Any(t => t.ScheduleValidatorType is { } type && !typeof(IPluginTaskValidator).IsAssignableFrom(type)))
                    throw new InvalidOperationException("工具计划校验器未实现契约。");
                if (registration.HealthChecks.Any(t => !typeof(IPluginHealthCheck).IsAssignableFrom(t)))
                    throw new InvalidOperationException("健康检查未实现插件契约。");
                var settingKeys = registration.ConfigurationProviders.SelectMany(p => p.Settings).Select(s => s.Key).ToArray();
                if (settingKeys.Intersect(ManagedSettingsStore.CaptureBaseline(configuration).Keys, StringComparer.OrdinalIgnoreCase).Any() ||
                    settingKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != settingKeys.Length ||
                    settingKeys.Intersect(runtime.plugins.Values.Where(p => p.Registration is not null).SelectMany(p => p.Registration!.ConfigurationProviders).SelectMany(p => p.Settings).Select(s => s.Key), StringComparer.OrdinalIgnoreCase).Any())
                    throw new InvalidOperationException("插件配置键冲突。");
                var probe = new RegistrationServiceProbe(services, staged);
                var toolNames = new List<string>();
                var promptNames = new List<string>();
                var resourceNames = new List<string>();
                foreach (var capability in registration.CapabilityAssemblies.Distinct())
                    foreach (var type in capability.GetTypes())
                        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
                        {
                            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null && method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                                toolNames.Add(McpServerTool.Create(method, (RequestContext<CallToolRequestParams> _) => null!, new() { Services = probe }).ProtocolTool.Name);
                            if (type.GetCustomAttribute<McpServerPromptTypeAttribute>() is not null && method.GetCustomAttribute<McpServerPromptAttribute>() is not null)
                                promptNames.Add(McpServerPrompt.Create(method, (RequestContext<GetPromptRequestParams> _) => null!, new() { Services = probe }).ProtocolPrompt.Name);
                            if (type.GetCustomAttribute<McpServerResourceTypeAttribute>() is not null && method.GetCustomAttribute<McpServerResourceAttribute>() is not null)
                                resourceNames.Add(McpServerResource.Create(method, (RequestContext<ReadResourceRequestParams> _) => null!, new() { Services = probe }).ProtocolResourceTemplate.UriTemplate);
                        }
                CheckUnique(toolNames, runtime.tools.Keys.Concat(new[] { "kaung_web_search", "kaung_web_fetch" }));
                CheckUnique(promptNames, runtime.prompts.Keys);
                CheckUnique(resourceNames, runtime.resources.Keys);
                if (registration.Tools.Select(t => t.Name).Distinct().Count() != registration.Tools.Count || registration.Tools.Any(t => !toolNames.Contains(t.Name)))
                    throw new InvalidOperationException("工具策略引用了未知或重复工具。");
                foreach (var name in toolNames) runtime.tools.Add(name, (manifest.Id, registration.Tools.SingleOrDefault(t => t.Name == name) ?? new(name)));
                foreach (var name in promptNames) runtime.prompts.Add(name, manifest.Id);
                foreach (var name in resourceNames) runtime.resources.Add(name, manifest.Id);
                foreach (var descriptor in staged) services.Add(descriptor);
                plugin.Registration = registration;
                plugin.Status = "loaded";
            }
            catch (Exception error)
            {
                plugin ??= new(new(Path.GetFileName(folder), Path.GetFileName(folder), "unknown", 0, "", ""), folder, Path.Combine(dataDirectory, Path.GetFileName(folder)));
                runtime.plugins.TryAdd(plugin.Manifest.Id, plugin);
                plugin.Status = "failed";
                plugin.Error = error.GetBaseException().Message;
            }
        }
        return runtime;
    }
    private static void CheckUnique(List<string> names, IEnumerable<string> existing)
    {
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Count || names.Intersect(existing, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("插件能力名称冲突。");
    }
    public static bool IsInstalledAndEnabled(IConfiguration configuration, string id)
    {
        var directory = Path.GetFullPath(configuration["Plugins:Directory"] ?? "plugins", AppContext.BaseDirectory);
        return File.Exists(Path.Combine(directory, id, "plugin.json")) && ReadEnabled(configuration, id);
    }
    private static bool ReadEnabled(IConfiguration configuration, string id)
    {
        var connectionString = configuration.GetConnectionString("KaungMcp") ?? "Data Source=data/host.db";
        // A pooled read-only handle must not become the first handle in the host's shared cache.
        var builder = new SqliteConnectionStringBuilder(connectionString)
            { Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false };
        if (!File.Exists(builder.DataSource)) return true;
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='PluginStates'";
        if (Convert.ToInt32(exists.ExecuteScalar()) == 0) return true;
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT IsEnabled FROM PluginStates WHERE Id=$id";
        command.Parameters.AddWithValue("$id", id);
        var value = command.ExecuteScalar();
        return value is null || Convert.ToBoolean(value);
    }
    public IMcpServerBuilder RegisterMcp(IMcpServerBuilder builder)
    {
        foreach (var plugin in plugins.Values.Where(p => p.Registration is not null))
            foreach (var assembly in plugin.Registration!.CapabilityAssemblies.Distinct())
                builder.WithToolsFromAssembly(assembly).WithPromptsFromAssembly(assembly).WithResourcesFromAssembly(assembly);
        return builder;
    }
    public async Task InitializeAsync(IServiceProvider services, CancellationToken ct)
    {
        var database = services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>();
        await using var db = await database.CreateDbContextAsync(ct);
        foreach (var plugin in plugins.Values)
        {
            var saved = await db.PluginStates.FindAsync([plugin.Manifest.Id], ct);
            if (saved is null) db.PluginStates.Add(new() { Id = plugin.Manifest.Id, Name = plugin.Manifest.Name, Version = plugin.Manifest.Version, IsEnabled = plugin.IsEnabled });
            else { saved.Name = plugin.Manifest.Name; saved.Version = plugin.Manifest.Version; }
            if (plugin.Status != "loaded") continue;
            try
            {
                foreach (var provider in plugin.Registration!.ConfigurationProviders)
                    services.GetRequiredService<ManagedSettingsStore>().RegisterProvider(provider, services.GetRequiredService<IConfiguration>());
                System.IO.Directory.CreateDirectory(plugin.DataDirectory);
                System.IO.Directory.CreateDirectory(Path.Combine(plugin.DataDirectory, "artifacts"));
                await plugin.Instance!.InitializeAsync(new(services, plugin.DataDirectory), ct);
                await using (var scope = services.CreateAsyncScope())
                {
                    foreach (var type in plugin.Registration.Tasks.Select(t => t.HandlerType)
                        .Concat(plugin.Registration.Tools.Where(t => t.ScheduleValidatorType is not null).Select(t => t.ScheduleValidatorType!))
                        .Concat(plugin.Registration.HealthChecks).Distinct())
                        _ = scope.ServiceProvider.GetRequiredService(type);
                }
                plugin.Status = "ready";
            }
            catch (Exception error) { plugin.Status = "failed"; plugin.Error = error.GetBaseException().Message; }
        }
        await db.SaveChangesAsync(ct);
    }
    public async Task<bool> SetEnabledAsync(string id, bool enabled, IServiceProvider services, CancellationToken ct)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>().CreateDbContextAsync(ct);
        var state = await db.PluginStates.FindAsync([id], ct);
        if (state is null) return false;
        state.IsEnabled = enabled;
        await db.SaveChangesAsync(ct);
        if (plugins.TryGetValue(id, out var plugin)) plugin.IsEnabled = enabled;
        healthCache.TryRemove(id, out _);
        return true;
    }
    public async Task<object[]> CatalogAsync(IServiceProvider services, CancellationToken ct)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>().CreateDbContextAsync(ct);
        var states = await db.PluginStates.AsNoTracking().Where(p => p.Id != "web").OrderBy(p => p.Id).ToListAsync(ct);
        var results = new List<object>();
        foreach (var state in states)
        {
            var plugin = plugins.GetValueOrDefault(state.Id);
            var health = new List<PluginHealth>();
            if (Available(state.Id) && healthCache.TryGetValue(state.Id, out var cached) && DateTimeOffset.UtcNow - cached.Time < TimeSpan.FromSeconds(30))
                health.AddRange(cached.Health);
            else if (Available(state.Id))
            {
                await using var scope = services.CreateAsyncScope();
                foreach (var check in plugin!.Registration!.HealthChecks)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromSeconds(15));
                        health.Add(await ((IPluginHealthCheck)scope.ServiceProvider.GetRequiredService(check)).CheckAsync(timeout.Token));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) { health.Add(new("degraded", error.GetBaseException().Message)); }
                }
            }
            if (Available(state.Id) && (!healthCache.TryGetValue(state.Id, out var recent) || DateTimeOffset.UtcNow - recent.Time >= TimeSpan.FromSeconds(30)))
                healthCache[state.Id] = (DateTimeOffset.UtcNow, health.ToArray());
            results.Add(new
            {
                state.Id,
                ownerId = state.Id, ownerKind = "plugin", ownerName = state.Name,
                state.Name,
                state.Version,
                state.IsEnabled,
                installed = plugin is not null,
                available = Available(state.Id),
                status = plugin?.Status ?? "missing",
                plugin?.Error,
                plugin?.Directory,
                pendingRestart = state.IsEnabled && plugin?.Status == "disabled",
                health,
                tools = tools.Where(t => t.Value.PluginId == state.Id).Select(t => t.Key).ToArray(),
                taskCount = plugin?.Registration?.Tasks.Count ?? 0
            });
        }
        return results.ToArray();
    }
    public object Definitions(string id) => new
    {
        tasks = plugins.GetValueOrDefault(id)?.Registration?.Tasks.Select(t => new { t.Id, t.Name, t.ParameterSchema, t.Version, t.DefaultTimeoutSeconds, t.Idempotent, t.MaxTimeoutSeconds }).ToArray(),
        templates = plugins.GetValueOrDefault(id)?.Registration?.DefaultTasks,
        settings = plugins.GetValueOrDefault(id)?.Registration?.ConfigurationProviders.SelectMany(p => p.Settings).ToArray()
    };
    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var plugin in plugins.Values.Where(p => p.Instance is not null))
        {
            try { await plugin.Instance!.StopAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception error) { plugin.Error = "停止插件失败：" + error.GetBaseException().Message; }
        }
    }
}
// Probe DI membership while validating SDK capability signatures, without creating business services.
internal sealed class RegistrationServiceProbe(IServiceCollection host, IServiceCollection staged) : IServiceProvider, IServiceProviderIsService
{
    public object? GetService(Type serviceType) => serviceType == typeof(IServiceProviderIsService) ? this : null;
    public bool IsService(Type serviceType) => serviceType == typeof(IServiceProvider) || serviceType == typeof(IServiceProviderIsService) ||
        serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>) ||
        host.Concat(staged).Any(d => d.ServiceType == serviceType || serviceType.IsGenericType && d.ServiceType == serviceType.GetGenericTypeDefinition());
}
internal sealed class PluginLoadContext(string path) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver resolver = new(path);
    protected override Assembly? Load(AssemblyName name)
    {
        // Share all framework and contract types crossing DI/MCP boundaries.
        if (name.Name is "KaungMcp.Plugin.Abstractions" or "KaungMcp.Foundation" ||
            name.Name?.StartsWith("ModelContextProtocol", StringComparison.Ordinal) == true ||
            name.Name?.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) == true ||
            name.Name?.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal) == true ||
            name.Name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true ||
            name.Name?.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal) == true ||
            name.Name?.StartsWith("SQLitePCLRaw", StringComparison.Ordinal) == true)
            return AssemblyLoadContext.Default.LoadFromAssemblyName(name);
        var resolved = resolver.ResolveAssemblyToPath(name);
        return resolved is null ? null : LoadFromAssemblyPath(resolved);
    }
    protected override IntPtr LoadUnmanagedDll(string name) => resolver.ResolveUnmanagedDllToPath(name) is { } resolved
        ? LoadUnmanagedDllFromPath(resolved) : IntPtr.Zero;
}
