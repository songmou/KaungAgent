using KaungMcp.Data;
using KaungMcp.Data.Entities;
using KaungMcp.Plugin;
using KaungMcp.Plugins;
using KaungMcp.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace KaungMcp.Capabilities;

public sealed record CapabilityOwner(string Id, string Kind, string Name, string Version, bool Available);

// Web is a host module. Plugins retain their own loading and lifetime boundaries.
public sealed class CapabilityRegistry(PluginRuntime plugins, IConfiguration configuration, IEnumerable<McpServerTool> registeredTools)
{
    private static readonly string[] WebTools = ["kaung_web_search", "kaung_web_fetch"];
    public string? ToolOwner(string name) => WebTools.Contains(name) ? "web" : plugins.ToolOwner(name);
    public PluginToolPolicy? ToolPolicy(string name) => WebTools.Contains(name) ? new(name, true) : plugins.ToolPolicy(name);
    public bool Available(string? id) => id == "web" || plugins.Available(id);
    public string Kind(string id) => id == "web" ? "module" : "plugin";
    public string Name(string id) => id == "web" ? "网页能力" : plugins.Plugins.FirstOrDefault(p => p.Manifest.Id == id)?.Manifest.Name ?? id;
    public string? Version(string id) => id == "web" ? "1.0.0" : plugins.Plugins.FirstOrDefault(p => p.Manifest.Id == id)?.Manifest.Version;
    public string TargetName(string id, string target) => registeredTools.FirstOrDefault(t => t.ProtocolTool.Name == target)?.ProtocolTool.Title
        ?? plugins.TaskDefinition(id, target)?.Name ?? (target == "kaung_web_search" ? "网页搜索" : target == "kaung_web_fetch" ? "网页读取" : target);
    public bool DefaultEnabled(string name) => name switch
    {
        "kaung_web_search" => configuration.GetValue("WebTools:SearchEnabled", true),
        "kaung_web_fetch" => configuration.GetValue("WebTools:FetchEnabled", true),
        _ => true
    };
    public string? ResolveOwner(string method, string? target)
    {
        if (target is null) return method is "initialize" or "ping" || method.EndsWith("/list") || method == "server/discover" ? "host" : null;
        if (method == "tools/call")
        {
            if (!target.Contains('/')) return ToolOwner(target);
            var parts = target.Split('/', 2);
            var owner = ToolOwner(parts[1]);
            return owner == parts[0] ? owner : null;
        }
        if (method == "tasks/execute" && target.Contains('/'))
        {
            var parts = target.Split('/', 2);
            return plugins.TaskDefinition(parts[0], parts[1]) is not null ? parts[0] : null;
        }
        return plugins.CapabilityOwner(method, target);
    }
    public async Task<CapabilityOwner[]> CatalogAsync(KaungMcpDbContext db, CancellationToken ct)
    {
        var states = await db.PluginStates.AsNoTracking().Where(p => p.Id != "web").ToListAsync(ct);
        return [new("web", "module", "网页能力", "1.0.0", true), .. states.Select(p => new CapabilityOwner(p.Id, "plugin", p.Name, p.Version, Available(p.Id)))];
    }
    public async Task InitializeAsync(KaungMcpDbContext db, CancellationToken ct)
    {
        // Removing this obsolete row is the transactional, one-time migration marker.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var old = await db.PluginStates.FindAsync(["web"], ct);
        if (old is not null)
        {
            if (!old.IsEnabled)
                foreach (var tool in WebTools)
                    if (!await db.ToolSettings.AnyAsync(t => t.ToolName == tool, ct))
                        db.ToolSettings.Add(new() { ToolName = tool, IsEnabled = false, UpdatedAtUtc = DateTimeOffset.UtcNow });
            db.PluginStates.Remove(old);
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // Backfill all scheduler attempts, rather than only the last invocationLogId.
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE McpInvocationLogs SET OwnerId = (SELECT PluginId FROM TaskRuns WHERE TaskRuns.CorrelationId = McpInvocationLogs.CorrelationId LIMIT 1),
                OwnerKind = CASE WHEN (SELECT PluginId FROM TaskRuns WHERE TaskRuns.CorrelationId = McpInvocationLogs.CorrelationId LIMIT 1) = 'web' THEN 'module' ELSE 'plugin' END
            WHERE OwnerId IS NULL AND CorrelationId IS NOT NULL AND EXISTS (SELECT 1 FROM TaskRuns WHERE TaskRuns.CorrelationId = McpInvocationLogs.CorrelationId)
            """, ct);
        var pending = db.InvocationLogs.Where(l => l.OwnerId == null);
        // Stream bounded batches. Unknown targets remain unknown and are not guessed from naming conventions.
        long after = 0;
        while (true)
        {
            var batch = await pending.Where(l => l.Id > after).OrderBy(l => l.Id).Take(500).ToListAsync(ct);
            if (batch.Count == 0) break;
            foreach (var log in batch)
            {
                log.OwnerId = ResolveOwner(log.RequestMethod, log.TargetName);
                log.OwnerKind = log.OwnerId is null ? null : log.OwnerId == "host" ? "host" : Kind(log.OwnerId);
            }
            after = batch[^1].Id;
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }
    public static bool LaunchToolEnabled(IConfiguration configuration, string tool, bool fallback)
    {
        var cs = new SqliteConnectionStringBuilder(configuration.GetConnectionString("KaungMcp") ?? "Data Source=data/host.db") { Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Private, Pooling = false };
        if (!File.Exists(cs.DataSource)) return fallback;
        using var db = new SqliteConnection(cs.ToString()); db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='McpToolSettings'";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            command.CommandText = "SELECT IsEnabled FROM McpToolSettings WHERE ToolName=$tool";
            command.Parameters.AddWithValue("$tool", tool);
            if (command.ExecuteScalar() is { } saved) return Convert.ToBoolean(saved);
        }
        command.Parameters.Clear(); command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name='PluginStates'";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0)
        {
            command.CommandText = "SELECT IsEnabled FROM PluginStates WHERE Id='web'";
            if (command.ExecuteScalar() is { } legacy && !Convert.ToBoolean(legacy)) return false;
        }
        return fallback;
    }
}
