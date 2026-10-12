using System.Net.Http.Json;
using KaungMcp.Configuration;
using KaungMcp.Security;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
namespace KaungMcp.Tests;

public sealed class StartupDatabaseTests
{
    [Fact]
    public async Task Existing_shared_cache_database_can_restart_after_plugin_state_probes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kaung-startup-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={path};Cache=Shared;Default Timeout=30";
        var code = path + ".code";
        Directory.CreateDirectory(code);
        Directory.CreateSymbolicLink(Path.Combine(code, "diagnostics"), Path.Combine(AppContext.BaseDirectory, "plugins", "diagnostics"));
        McpWebApplicationFactory Host() => new(codeDirectory: code, databasePath: path, preserveData: true,
            configureConfiguration: c => c.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:KaungMcp"] = connectionString }));
        try
        {
            await using (var first = Host())
            {
                using var client = first.CreateClient();
                (await client.GetAsync("/health/ready")).EnsureSuccessStatusCode();
            }
            using (var pool = new SqliteConnection(connectionString)) SqliteConnection.ClearPool(pool);
            await using (var restarted = Host())
            {
                using var client = restarted.CreateClient();
                (await client.GetAsync("/health/ready")).EnsureSuccessStatusCode();
                client.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
                (await client.PostAsJsonAsync("/api/admin/clients", new { name = "restart-write", description = "startup regression" })).EnsureSuccessStatusCode();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) File.Delete(f);
            foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), Path.GetFileName(path) + "*")) Directory.Delete(dir, true);
        }
    }
}
