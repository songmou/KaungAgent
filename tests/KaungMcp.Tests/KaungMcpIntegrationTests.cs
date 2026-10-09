using System.Net;
using System.Net.Http.Json;
using KaungMcp.Clients;
using KaungMcp.Data;
using KaungMcp.InvocationLogs;
using KaungMcp.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace KaungMcp.Tests;

public sealed class KaungMcpIntegrationTests
{
    [Fact]
    public async Task Client_lifecycle_tools_and_metadata_logs_work_end_to_end()
    {
        await using var factory = new McpWebApplicationFactory();
        using var anonymousClient = factory.CreateClient();
        var statusPage = await anonymousClient.GetAsync("/");
        statusPage.EnsureSuccessStatusCode();
        Assert.Contains("KaungMcp 正在运行", await statusPage.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var unauthorized = await anonymousClient.GetAsync("/api/admin/clients");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var adminClient = factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var createResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/clients",
            new CreateClientRequest("integration-agent", "Integration test client"));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedClientResponse>();
        Assert.NotNull(created);
        Assert.StartsWith("kmcp_", created.ApiKey, StringComparison.Ordinal);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>();
            await using var dbContext = await dbFactory.CreateDbContextAsync();
            var stored = await dbContext.ClientApplications.SingleAsync(item => item.Id == created.Id);
            Assert.Equal(32, stored.KeyHash.Length);
            Assert.DoesNotContain(created.ApiKey, Convert.ToHexString(stored.KeyHash), StringComparison.Ordinal);
        }

        await using (var mcpClient = await CreateMcpClientAsync(factory, created.ApiKey))
        {
            Assert.NotNull(mcpClient.ServerCapabilities.Tools);
            Assert.NotNull(mcpClient.ServerCapabilities.Prompts);
            Assert.NotNull(mcpClient.ServerCapabilities.Resources);

            var tools = await mcpClient.ListToolsAsync();
            Assert.Contains(tools, tool => tool.Name == "kaung_ping");
            Assert.Contains(tools, tool => tool.Name == "kaung_echo");

            var prompts = await mcpClient.ListPromptsAsync();
            Assert.Contains(prompts, prompt => prompt.Name == "kaung_connection_diagnostic");
            var diagnostic = await mcpClient.GetPromptAsync("kaung_connection_diagnostic");
            Assert.Contains("kaung_ping", diagnostic.Messages.Single().Content.ToString(), StringComparison.Ordinal);
            await Assert.ThrowsAnyAsync<Exception>(async () => await mcpClient.GetPromptAsync("missing_prompt"));

            var resources = await mcpClient.ListResourcesAsync();
            Assert.Contains(resources, resource => resource.Uri == "kaung://server/status");
            var status = await mcpClient.ReadResourceAsync("kaung://server/status");
            Assert.Contains("\"status\":\"ok\"", status.Contents.Single().ToString(), StringComparison.Ordinal);

            var echoResult = await mcpClient.CallToolAsync(
                "kaung_echo",
                new Dictionary<string, object?> { ["message"] = "hello Kaung" });
            Assert.NotEqual(true, echoResult.IsError);
            Assert.Contains("hello Kaung", echoResult.StructuredContent?.GetRawText(), StringComparison.Ordinal);
        }

        // Streamed responses can reach the SDK before HTTP completion persists their logs.
        var logs = await McpWebApplicationFactory.WaitForLogsAsync(adminClient, created.Id, items =>
            items.Any(item => item.ToolName == "kaung_echo") &&
            items.Any(item => item.RequestMethod is "server/discover" or "tools/list") &&
            items.Any(item => item.RequestMethod == "prompts/get" && item.TargetName == "missing_prompt") &&
            items.Any(item => item.RequestMethod == "resources/read"));
        Assert.NotNull(logs);
        var invocation = Assert.Single(logs.Items, item => item.ToolName == "kaung_echo");
        Assert.Equal("kaung_echo", invocation.ToolName);
        Assert.Equal("tools/call", invocation.RequestMethod);
        Assert.True(invocation.Succeeded);
        Assert.Null(invocation.InputTokens);
        Assert.Null(invocation.OutputTokens);
        Assert.Null(invocation.TotalTokens);
        Assert.Contains(logs.Items, item => item.RequestMethod is "server/discover" or "tools/list");
        Assert.Contains(logs.Items, item => item.RequestMethod == "prompts/get");
        Assert.Contains(logs.Items, item => item.RequestMethod == "prompts/get" && item.TargetName == "missing_prompt" && !item.Succeeded);
        Assert.Contains(logs.Items, item => item.RequestMethod == "resources/read");

        var disableResponse = await adminClient.PostAsync($"/api/admin/clients/{created.Id}/disable", null);
        disableResponse.EnsureSuccessStatusCode();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await using var unused = await CreateMcpClientAsync(factory, created.ApiKey);
        });

        var enableResponse = await adminClient.PostAsync($"/api/admin/clients/{created.Id}/enable", null);
        enableResponse.EnsureSuccessStatusCode();
        var rotatedResponse = await adminClient.PostAsync($"/api/admin/clients/{created.Id}/rotate-key", null);
        rotatedResponse.EnsureSuccessStatusCode();
        var rotated = await rotatedResponse.Content.ReadFromJsonAsync<RotatedClientKeyResponse>();
        Assert.NotNull(rotated);
        Assert.NotEqual(created.ApiKey, rotated.ApiKey);
        await using var rotatedClient = await CreateMcpClientAsync(factory, rotated.ApiKey);
        var rotatedTools = await rotatedClient.ListToolsAsync();
        Assert.Contains(rotatedTools, tool => tool.Name == "kaung_ping");
        var continuousLogs = await adminClient.GetFromJsonAsync<PagedResponse<InvocationLogResponse>>(
            $"/api/admin/call-logs?clientId={created.Id}&method=tools/call&pageSize=100");
        Assert.NotNull(continuousLogs);
        Assert.Contains(continuousLogs.Items, item => item.ToolName == "kaung_echo");
        Assert.All(continuousLogs.Items, item => Assert.Equal(created.Id, item.ClientApplicationId));
        var failedPrompts = await adminClient.GetFromJsonAsync<PagedResponse<InvocationLogResponse>>(
            $"/api/admin/call-logs?clientId={created.Id}&method=prompts/get&succeeded=false&page=1&pageSize=1");
        Assert.NotNull(failedPrompts);
        Assert.Equal(1, failedPrompts.PageSize);
        Assert.Contains(failedPrompts.Items, item => item.TargetName == "missing_prompt");

        var deleteResponse = await adminClient.DeleteAsync($"/api/admin/clients/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await using var unused = await CreateMcpClientAsync(factory, rotated.ApiKey);
        });
    }

    private static async Task<McpClient> CreateMcpClientAsync(
        McpWebApplicationFactory factory,
        string apiKey)
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
