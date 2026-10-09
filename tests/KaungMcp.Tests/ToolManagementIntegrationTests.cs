using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KaungMcp.Clients;
using KaungMcp.InvocationLogs;
using KaungMcp.Security;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KaungMcp.Tests;

public sealed class ToolManagementIntegrationTests
{
    [Fact]
    public async Task Catalog_enablement_admin_test_and_logs_work_end_to_end()
    {
        await using var factory = new McpWebApplicationFactory();
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/tools")).StatusCode);

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);

        var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/admin/tools");
        Assert.True(catalog.GetProperty("totalCount").GetInt32() >= 11);
        var categories = catalog.GetProperty("categories").EnumerateArray().ToArray();
        Assert.Contains(categories, item => item.GetProperty("id").GetString() == "diagnostics");
        Assert.Contains(categories, item => item.GetProperty("id").GetString() == "web");
        Assert.Contains(categories, item => item.GetProperty("id").GetString() == "jewelry");
        var echo = categories.SelectMany(item => item.GetProperty("tools").EnumerateArray())
            .Single(item => item.GetProperty("name").GetString() == "kaung_echo");
        Assert.True(echo.GetProperty("isEnabled").GetBoolean());
        Assert.True(echo.GetProperty("readOnly").GetBoolean());
        Assert.True(echo.GetProperty("inputSchema").GetProperty("properties").TryGetProperty("message", out _));

        var created = await (await admin.PostAsJsonAsync("/api/admin/clients",
            new CreateClientRequest("tool-management-client", null))).Content.ReadFromJsonAsync<CreatedClientResponse>();
        Assert.NotNull(created);
        await using var mcpClient = await CreateMcpClientAsync(factory, created.ApiKey);
        Assert.Contains(await mcpClient.ListToolsAsync(), tool => tool.Name == "kaung_echo");

        var disabled = await admin.PutAsJsonAsync("/api/admin/tools/kaung_echo/enabled", new { isEnabled = false });
        disabled.EnsureSuccessStatusCode();
        Assert.DoesNotContain(await mcpClient.ListToolsAsync(), tool => tool.Name == "kaung_echo");
        var disabledResult = await mcpClient.CallToolAsync("kaung_echo",
            new Dictionary<string, object?> { ["message"] = "must be blocked" });
        Assert.True(disabledResult.IsError);

        var testResponse = await admin.PostAsJsonAsync("/api/admin/tools/kaung_echo/test", new
        {
            arguments = new { message = "admin console" },
            confirmRisk = false
        });
        testResponse.EnsureSuccessStatusCode();
        var testResult = await testResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(testResult.GetProperty("succeeded").GetBoolean(), testResult.GetRawText());
        Assert.False(testResult.GetProperty("isEnabled").GetBoolean());
        Assert.Contains("admin console",
            testResult.GetProperty("result").GetProperty("structuredContent").GetRawText(),
            StringComparison.Ordinal);
        Assert.True(testResult.GetProperty("logId").GetInt64() > 0);

        var logs = await admin.GetFromJsonAsync<PagedResponse<InvocationLogResponse>>(
            "/api/admin/call-logs?source=admin_test&toolName=kaung_echo");
        Assert.NotNull(logs);
        var log = Assert.Single(logs.Items);
        Assert.Null(log.ClientApplicationId);
        Assert.Equal("后台测试", log.ClientName);
        Assert.Equal(InvocationSources.AdminTest, log.Source);
        Assert.True(log.Succeeded);
        var detail = await admin.GetFromJsonAsync<InvocationLogDetailResponse>($"/api/admin/call-logs/{log.Id}");
        Assert.NotNull(detail);
        Assert.Contains("admin console", detail.RequestParametersJson, StringComparison.Ordinal);
        Assert.Contains("admin console", detail.ResponseBody, StringComparison.Ordinal);

        (await admin.PutAsJsonAsync("/api/admin/tools/kaung_echo/enabled", new { isEnabled = true }))
            .EnsureSuccessStatusCode();
        Assert.Contains(await mcpClient.ListToolsAsync(), tool => tool.Name == "kaung_echo");
    }

    [Fact]
    public async Task Tool_management_rejects_unknown_tools_and_non_object_arguments()
    {
        await using var factory = new McpWebApplicationFactory();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);

        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PutAsJsonAsync("/api/admin/tools/missing/enabled", new { isEnabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await admin.PostAsJsonAsync("/api/admin/tools/missing/test", new { arguments = new { } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/admin/tools/kaung_echo/test", new { arguments = new[] { "bad" } })).StatusCode);

        var toolError = await admin.PostAsJsonAsync("/api/admin/tools/kaung_echo/test", new
        {
            arguments = new { },
            confirmRisk = false
        });
        toolError.EnsureSuccessStatusCode();
        var toolErrorResult = await toolError.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(toolErrorResult.GetProperty("succeeded").GetBoolean());
        Assert.True(toolErrorResult.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task Risky_tool_requires_explicit_confirmation()
    {
        await using var factory = new McpWebApplicationFactory();
        await using var scope = factory.Services.CreateAsyncScope();
        var echo = scope.ServiceProvider.GetServices<McpServerTool>()
            .Single(tool => tool.ProtocolTool.Name == "kaung_echo");
        echo.ProtocolTool.Annotations = new ToolAnnotations
        {
            ReadOnlyHint = false,
            DestructiveHint = true,
            IdempotentHint = true
        };

        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var blocked = await admin.PostAsJsonAsync("/api/admin/tools/kaung_echo/test", new
        {
            arguments = new { message = "confirmed later" },
            confirmRisk = false
        });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);

        var confirmed = await admin.PostAsJsonAsync("/api/admin/tools/kaung_echo/test", new
        {
            arguments = new { message = "confirmed later" },
            confirmRisk = true
        });
        confirmed.EnsureSuccessStatusCode();
        Assert.True((await confirmed.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("succeeded").GetBoolean());
    }

    private static async Task<McpClient> CreateMcpClientAsync(McpWebApplicationFactory factory, string apiKey)
    {
        var httpClient = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {apiKey}"
                }
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }
}
