using System.Net.Http.Json;
using KaungMcp.Clients;
using KaungMcp.InvocationLogs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using KaungMcp.Plugins;
using ModelContextProtocol.Server;

namespace KaungMcp.Tests;

public sealed class McpWebApplicationFactory(
    Action<IServiceCollection>? configureTestServices = null, bool jewelryOnly = false, string[]? pluginIds = null, string? databasePath = null,
    string? codeDirectory = null, bool preserveData = false, bool shareLoadedAssemblies = true,
    Action<IConfigurationBuilder>? configureConfiguration = null) : WebApplicationFactory<KaungMcpApplicationMarker>
{
    private static readonly System.Reflection.Assembly[] SharedPluginAssemblies =
        [typeof(KaungMcp.Plugins.Jewelry.JewelryPlugin).Assembly, typeof(KaungMcp.Plugins.Diagnostics.DiagnosticsPlugin).Assembly];
    public const string AdminKey = "test-admin-key-that-is-at-least-32-characters";

    public string DatabasePath { get; } = databasePath ?? Path.Combine(
        Path.GetTempPath(),
        $"kaungmcp-tests-{Guid.NewGuid():N}.db");

    public static async Task<PagedResponse<InvocationLogResponse>> WaitForLogsAsync(
        HttpClient admin, Guid clientId, Func<IReadOnlyList<InvocationLogResponse>, bool> ready)
    {
        for (var attempt = 0; ; attempt++)
        {
            var logs = await admin.GetFromJsonAsync<PagedResponse<InvocationLogResponse>>(
                $"/api/admin/call-logs?clientId={clientId}&pageSize=100");
            Assert.NotNull(logs);
            if (ready(logs.Items)) return logs;
            Assert.True(attempt < 100, "Expected invocation logs were not persisted within the timeout.");
            await Task.Delay(25);
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _ = SharedPluginAssemblies.Length;
        builder.UseEnvironment("Testing");
        var pluginDirectory = codeDirectory ?? Path.Combine(AppContext.BaseDirectory, "plugins");
        if (codeDirectory is null && (jewelryOnly || pluginIds is not null))
        {
            pluginDirectory = DatabasePath + ".code";
            Directory.CreateDirectory(pluginDirectory);
            foreach (var id in (pluginIds ?? ["jewelry"]).Where(id => id != "web")) Directory.CreateSymbolicLink(Path.Combine(pluginDirectory, id), Path.Combine(AppContext.BaseDirectory, "plugins", id));
        }
        IConfiguration? testConfiguration = null;
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminAuth:Key"] = AdminKey,
                ["Plugins:Directory"] = pluginDirectory,
                ["WebTools:SearchEnabled"] = (!jewelryOnly && codeDirectory is null && (pluginIds is null || pluginIds.Contains("web"))).ToString(),
                ["WebTools:FetchEnabled"] = (!jewelryOnly && codeDirectory is null && (pluginIds is null || pluginIds.Contains("web"))).ToString(),
                ["Plugins:DataDirectory"] = DatabasePath + ".plugins",
                ["Plugins:ShareLoadedAssemblies"] = shareLoadedAssemblies.ToString(),
                ["AllowedHosts"] = "*",
                ["ConnectionStrings:KaungMcp"] = $"Data Source={DatabasePath};Default Timeout=30"
                ,
                ["McpManagement:SettingsPath"] = DatabasePath + ".settings.json"
                ,
                ["McpManagement:LocalEnvPath"] = DatabasePath + ".local.env"
                ,
                ["McpManagement:CertificatePath"] = DatabasePath + ".lan.pfx"
                ,
                ["Logging:LogLevel:Default"] = "Warning"
            });
            configureConfiguration?.Invoke(configuration);
            testConfiguration = configuration.Build();
        });
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<PluginRuntime>();
            services.RemoveAll<McpServerTool>();
            services.RemoveAll<McpServerPrompt>();
            services.RemoveAll<McpServerResource>();
            var runtime = PluginRuntime.Discover(services, testConfiguration!);
            services.AddSingleton(runtime);
            runtime.RegisterMcp(services.AddMcpServer());
            services.AddMcpServer().WithToolsFromAssembly(typeof(KaungMcp.Web.WebModule).Assembly);
            configureTestServices?.Invoke(services);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || preserveData)
        {
            return;
        }

        if (Directory.Exists(DatabasePath + ".plugins")) Directory.Delete(DatabasePath + ".plugins", true);
        if (Directory.Exists(DatabasePath + ".code")) Directory.Delete(DatabasePath + ".code", true);
        foreach (var path in new[] { DatabasePath, $"{DatabasePath}-wal", $"{DatabasePath}-shm",
                     DatabasePath + ".settings.json", DatabasePath + ".local.env", DatabasePath + ".lan.pfx" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
