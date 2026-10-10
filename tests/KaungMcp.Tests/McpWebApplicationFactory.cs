using System.Net.Http.Json;
using KaungMcp.Clients;
using KaungMcp.InvocationLogs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KaungMcp.Tests;

public sealed class McpWebApplicationFactory(
    Action<IServiceCollection>? configureTestServices = null, bool jewelryOnly = false) : WebApplicationFactory<KaungMcpApplicationMarker>
{
    public const string AdminKey = "test-admin-key-that-is-at-least-32-characters";

    public string DatabasePath { get; } = Path.Combine(
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
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminAuth:Key"] = AdminKey,
                ["McpExposure:JewelryOnly"] = jewelryOnly.ToString(),
                ["AllowedHosts"] = "*",
                ["ConnectionStrings:KaungMcp"] = $"Data Source={DatabasePath};Default Timeout=30"
                , ["McpManagement:SettingsPath"] = DatabasePath + ".settings.json"
                , ["McpManagement:LocalEnvPath"] = DatabasePath + ".local.env"
                , ["McpManagement:CertificatePath"] = DatabasePath + ".lan.pfx"
            }));
        if (configureTestServices is not null)
        {
            builder.ConfigureTestServices(configureTestServices);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

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
