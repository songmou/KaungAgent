using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KaungMcp.Clients;
using KaungMcp.Configuration;
using KaungMcp.Security;
using KaungMcp.Web;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KaungMcp.Tests;

public sealed class ManagementIntegrationTests
{
    [Fact]
    public async Task Authenticated_initialize_request_is_logged_with_payload()
    {
        await using var factory = new McpWebApplicationFactory();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var created = await (await admin.PostAsJsonAsync("/api/admin/clients",
            new CreateClientRequest("raw-init-client", null))).Content.ReadFromJsonAsync<CreatedClientResponse>();
        Assert.NotNull(created);

        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", created.ApiKey);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        request.Content = new StringContent("""
            {"jsonrpc":"2.0","id":77,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"raw-test","version":"1.0"}}}
            """, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);

        var logs = await admin.GetFromJsonAsync<PagedResponse<KaungMcp.InvocationLogs.InvocationLogResponse>>(
            $"/api/admin/call-logs?clientId={created.Id}");
        Assert.NotNull(logs);
        var entry = Assert.Single(logs.Items, item => item.RequestMethod == "initialize");
        Assert.Null(entry.TargetName);
        Assert.Null(entry.ToolName);
        var detail = await admin.GetFromJsonAsync<KaungMcp.InvocationLogs.InvocationLogDetailResponse>(
            $"/api/admin/call-logs/{entry.Id}");
        Assert.NotNull(detail);
        Assert.Contains("raw-test", detail.RequestParametersJson, StringComparison.Ordinal);
        Assert.Contains("serverInfo", detail.ResponseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(created.ApiKey, detail.ResponseBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Management_page_settings_and_admin_rotation_work()
    {
        await using var factory = new McpWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        var page = await anonymous.GetAsync("/admin/");
        page.EnsureSuccessStatusCode();
        Assert.Contains("管理工作台", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("工具管理", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/admin/settings/validate",
            new { changes = new Dictionary<string, string> { ["WebFetch:TimeoutSeconds"] = "18" } })).StatusCode);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var initial = await admin.GetFromJsonAsync<JsonElement>("/api/admin/settings");
        Assert.Equal("15", initial.GetProperty("effective").GetProperty("WebFetch:TimeoutSeconds").GetString());
        var preview = await admin.PostAsJsonAsync("/api/admin/settings/validate", new
        {
            changes = new Dictionary<string, string> { ["WebFetch:TimeoutSeconds"] = "18" }
        });
        preview.EnsureSuccessStatusCode();
        Assert.Equal("18", (await preview.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("desired").GetProperty("WebFetch:TimeoutSeconds").GetString());
        Assert.Equal("15", (await admin.GetFromJsonAsync<JsonElement>("/api/admin/settings"))
            .GetProperty("effective").GetProperty("WebFetch:TimeoutSeconds").GetString());

        var updated = await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            changes = new Dictionary<string, string>
            {
                ["WebFetch:TimeoutSeconds"] = "18",
                ["McpHost:LocalPort"] = "5101",
                ["McpHost:LanEnabled"] = "true",
                ["McpHost:LanAddress"] = "192.168.1.20"
            }
        });
        updated.EnsureSuccessStatusCode();
        var value = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("18", value.GetProperty("effective").GetProperty("WebFetch:TimeoutSeconds").GetString());
        Assert.Equal("5091", value.GetProperty("effective").GetProperty("McpHost:LocalPort").GetString());
        Assert.Equal("5101", value.GetProperty("desired").GetProperty("McpHost:LocalPort").GetString());
        Assert.Contains("McpHost:LocalPort", value.GetProperty("pendingRestart").EnumerateArray().Select(item => item.GetString()));
        Assert.NotEqual(JsonValueKind.Null, value.GetProperty("certificate").ValueKind);
        (await admin.GetAsync("/api/admin/certificate")).EnsureSuccessStatusCode();

        var launcherChange = await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            changes = new Dictionary<string, string> { ["SearXngLauncher:Mode"] = "disabled" }
        });
        launcherChange.EnsureSuccessStatusCode();
        var launcherSettings = await launcherChange.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("true", launcherSettings.GetProperty("effective").GetProperty("WebSearch:SearXng:Enabled").GetString(), ignoreCase: true);
        Assert.Equal("false", launcherSettings.GetProperty("desired").GetProperty("WebSearch:SearXng:Enabled").GetString());
        Assert.Contains("SearXngLauncher:Mode", launcherSettings.GetProperty("pendingRestart").EnumerateArray().Select(item => item.GetString()));

        var runtimeSearch = await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            changes = new Dictionary<string, string>
            {
                ["WebSearch:SearXng:Enabled"] = "false",
                ["WebSearch:SearXng:BaseUrl"] = "http://127.0.0.1:9099"
            }
        });
        runtimeSearch.EnsureSuccessStatusCode();
        var runtimeSettings = await runtimeSearch.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("false", runtimeSettings.GetProperty("effective").GetProperty("WebSearch:SearXng:Enabled").GetString());
        Assert.Equal("http://127.0.0.1:9099", runtimeSettings.GetProperty("effective").GetProperty("WebSearch:SearXng:BaseUrl").GetString());
        Assert.Equal("http://127.0.0.1:9099", factory.Services.GetRequiredService<ManagedSettingsStore>().SearchOptions.SearXng.BaseUrl);

        var invalidDatabase = await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            changes = new Dictionary<string, string> { ["ConnectionStrings:KaungMcp"] = "Data Source=/tmp/kaungmcp-no-such-db.db" }
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalidDatabase.StatusCode);

        var restored = await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            clear = new[] { "WebFetch:TimeoutSeconds" }
        });
        restored.EnsureSuccessStatusCode();
        var restoredValue = await restored.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("15", restoredValue.GetProperty("effective").GetProperty("WebFetch:TimeoutSeconds").GetString());

        (await admin.PutAsJsonAsync("/api/admin/settings", new
        {
            changes = new Dictionary<string, string> { ["WebSearch:StaticFallback:Enabled"] = "false" }
        })).EnsureSuccessStatusCode();
        var fallback = factory.Services.GetRequiredService<BingHtmlSearchProvider>();
        await Assert.ThrowsAsync<WebSearchException>(() => fallback.SearchAsync(
            new WebSearchRequest("test", 1, 1, "all", null, "moderate"), CancellationToken.None));

        const string replacement = "new-admin-secret-that-is-longer-than-32-characters";
        await File.WriteAllTextAsync(factory.DatabasePath + ".local.env",
            $"KAUNG_MCP_ADMIN_KEY={McpWebApplicationFactory.AdminKey}\nKAUNG_AGENT_API_KEY=unchanged\n");
        (await admin.PostAsJsonAsync("/api/admin/admin-key/rotate", new { newKey = replacement })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync("/api/admin/settings")).StatusCode);
        using var rotated = factory.CreateClient();
        rotated.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, replacement);
        (await rotated.GetAsync("/api/admin/settings")).EnsureSuccessStatusCode();
        Assert.True(File.Exists(factory.DatabasePath + ".settings.json"));
        Assert.Contains($"KAUNG_MCP_ADMIN_KEY={replacement}",
            await File.ReadAllTextAsync(factory.DatabasePath + ".local.env"), StringComparison.Ordinal);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var reloaded = new ManagedSettingsStore(configuration, ManagedSettingsStore.CaptureBaseline(configuration));
        Assert.True(reloaded.ValidateAdminKey(replacement, McpWebApplicationFactory.AdminKey));
        Assert.False(reloaded.ValidateAdminKey(McpWebApplicationFactory.AdminKey, McpWebApplicationFactory.AdminKey));
    }
}
