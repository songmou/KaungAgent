using System.Net.Http.Json;
using KaungMcp.Scheduling;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KaungMcp.Tests;

public sealed class SchedulerStartupTests
{
    [Fact]
    public async Task Development_host_starts_with_scheduler_worker_and_seeded_tasks()
    {
        await using var factory = new DevelopmentFactory();
        using var client = factory.CreateClient();
        var ready = await client.GetAsync("/health/ready");
        ready.EnsureSuccessStatusCode();
        var scheduler = factory.Services.GetRequiredService<SchedulerService>();
        var tasks = await scheduler.ListAsync(1, 20, "active", null, null, default);
        Assert.Equal(3, tasks.TotalCount);
        Assert.Equal(0, tasks.EnabledCount);
    }

    private sealed class DevelopmentFactory : WebApplicationFactory<KaungMcpApplicationMarker>
    {
        private readonly string databasePath = Path.Combine(Path.GetTempPath(), $"kaungmcp-startup-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["AdminAuth:Key"] = McpWebApplicationFactory.AdminKey,
                    ["AllowedHosts"] = "*",
                    ["ConnectionStrings:KaungMcp"] = $"Data Source={databasePath};Default Timeout=30",
                    ["McpManagement:SettingsPath"] = databasePath + ".settings.json",
                    ["McpManagement:LocalEnvPath"] = databasePath + ".local.env",
                    ["McpManagement:CertificatePath"] = databasePath + ".lan.pfx",
                    ["Jewelry:BackgroundCollection"] = "false",
                    ["Jewelry:Enabled"] = "false"
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
                if (File.Exists(path)) File.Delete(path);
        }
    }
}
