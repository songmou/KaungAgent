using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KaungMcp.Clients;
using KaungMcp.Data;
using KaungMcp.InvocationLogs;
using KaungMcp.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace KaungMcp.Tests;

public sealed class InvocationLogDetailTests
{
    [Fact]
    public async Task Details_capture_all_methods_actual_errors_and_keep_lists_lightweight()
    {
        await using var factory = new McpWebApplicationFactory();
        using var admin = CreateAdmin(factory);
        var client = await CreateClient(admin);
        using var http = factory.CreateClient();
        var requests = new (string Method, object? Parameters, bool Success)[]
        {
            ("initialize", new { protocolVersion = "2025-03-26", capabilities = new { }, clientInfo = new { name = "detail-test", version = "1.0" } }, true),
            ("ping", null, true),
            ("tools/list", null, true),
            ("prompts/list", null, true),
            ("prompts/get", new { name = "kaung_connection_diagnostic" }, true),
            ("resources/list", null, true),
            ("resources/templates/list", null, true),
            ("resources/read", new { uri = "kaung://server/status" }, true),
            ("tools/call", new { name = "kaung_echo", arguments = new { message = "你好 <script>alert(1)</script>" } }, true),
            ("tools/call", new { name = "kaung_echo", arguments = new { message = new string('x', 70_000) } }, false),
            ("prompts/get", new { name = "missing_prompt" }, false),
            ("unknown/method", null, false),
            ("server/discover", new { _meta = new Dictionary<string, object>
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new { name = "detail-test", version = "1.0" },
                ["io.modelcontextprotocol/clientCapabilities"] = new { }
            } }, true)
        };
        for (var index = 0; index < requests.Length; index++)
        {
            var (method, parameters, success) = requests[index];
            var request = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = index, method, @params = parameters });
            using var response = await Send(http, client.ApiKey, request, method == "server/discover" ? "2026-07-28" : null);
            var actualOutput = await response.Content.ReadAsStringAsync();
            var logs = await WaitForLogs(admin, client.Id, index + 1);
            var entry = logs.Items.OrderByDescending(item => item.Id).First();
            Assert.Equal(method, entry.RequestMethod);
            Assert.True(success == entry.Succeeded, $"Unexpected status for {method}: {actualOutput}");
            var detail = await admin.GetFromJsonAsync<InvocationLogDetailResponse>($"/api/admin/call-logs/{entry.Id}");
            Assert.NotNull(detail);
            Assert.Equal(JsonSerializer.Serialize(parameters), detail.RequestParametersJson);
            Assert.NotNull(detail.ResponseBody);
            using var output = JsonDocument.Parse(detail.ResponseBody);
            Assert.Equal(index, output.RootElement.GetProperty("id").GetInt32());
            Assert.Contains(detail.ResponseBody, actualOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(client.ApiKey, detail.ResponseBody, StringComparison.Ordinal);
            if (method == "resources/read") Assert.Equal("kaung://server/status", entry.TargetName);
            if (index == 9)
            {
                Assert.Equal("mcp_error", entry.ErrorCode);
                Assert.True(output.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
            }
            if (index >= 10 && !success) Assert.True(output.RootElement.TryGetProperty("error", out _));
        }

        var listJson = await admin.GetStringAsync($"/api/admin/call-logs?clientId={client.Id}");
        Assert.DoesNotContain("requestParametersJson", listJson, StringComparison.Ordinal);
        Assert.DoesNotContain("responseBody", listJson, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/admin/call-logs/9223372036854775807")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/admin/call-logs/1")).StatusCode);

        using var notification = await Send(http, client.ApiKey, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        using var unauthorized = await Send(http, "invalid", """{"jsonrpc":"2.0","id":77,"method":"ping"}""");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(requests.Length, (await WaitForLogs(admin, client.Id, requests.Length)).TotalCount);
    }

    [Fact]
    public async Task Concurrent_requests_with_identical_rpc_ids_stay_with_their_own_parameters()
    {
        await using var factory = new McpWebApplicationFactory();
        using var admin = CreateAdmin(factory);
        var client = await CreateClient(admin);
        using var http = factory.CreateClient();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(async index =>
        {
            using var response = await Send(http, client.ApiKey, JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id = 1, method = "tools/call",
                @params = new { name = "kaung_echo", arguments = new { message = $"concurrent-{index}" } }
            }));
            response.EnsureSuccessStatusCode();
        }));
        var logs = await WaitForLogs(admin, client.Id, 12);
        Assert.Equal(12, logs.TotalCount);
        var messages = new HashSet<string>();
        foreach (var entry in logs.Items)
        {
            var detail = await admin.GetFromJsonAsync<InvocationLogDetailResponse>($"/api/admin/call-logs/{entry.Id}");
            Assert.NotNull(detail);
            using var parameters = JsonDocument.Parse(detail.RequestParametersJson!);
            using var response = JsonDocument.Parse(detail.ResponseBody!);
            var message = parameters.RootElement.GetProperty("arguments").GetProperty("message").GetString()!;
            Assert.True(messages.Add(message));
            Assert.Equal(message, response.RootElement.GetProperty("result").GetProperty("structuredContent").GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task Streaming_cancellation_and_logging_failures_do_not_change_the_response()
    {
        await using var factory = new McpWebApplicationFactory();
        using var admin = CreateAdmin(factory);
        var client = await CreateClient(admin);
        using var scope = factory.Services.CreateScope();
        var logs = scope.ServiceProvider.GetRequiredService<McpInvocationLogService>();
        var context = MakeContext(scope.ServiceProvider, client.ApiKey);
        var originalBody = context.Response.Body;
        const string output = """{"jsonrpc":"2.0","id":1,"result":{"value":"actual"}}""";
        var middleware = new McpInvocationLoggingMiddleware(async ctx =>
        {
            ctx.Response.ContentType = "text/event-stream";
            await ctx.Response.WriteAsync("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/message\"}\n\n");
            await ctx.Response.Body.FlushAsync();
            Assert.True(originalBody.Length > 0); // Output is forwarded before the invocation finishes.
            await ctx.Response.WriteAsync($"data: {output}\n\n");
        }, NullLogger<McpInvocationLoggingMiddleware>.Instance);
        await middleware.InvokeAsync(context, logs);
        Assert.Same(originalBody, context.Response.Body);
        var entry = Assert.Single((await WaitForLogs(admin, client.Id, 1)).Items);
        var detail = await logs.GetAsync(entry.Id, CancellationToken.None);
        Assert.Equal("null", detail!.RequestParametersJson);
        Assert.Equal(output, detail.ResponseBody);

        var cancelled = MakeContext(scope.ServiceProvider, client.ApiKey);
        var cancellation = new OperationCanceledException("test cancellation");
        var cancelling = new McpInvocationLoggingMiddleware(_ => throw cancellation,
            NullLogger<McpInvocationLoggingMiddleware>.Instance);
        Assert.Same(cancellation, await Assert.ThrowsAsync<OperationCanceledException>(() => cancelling.InvokeAsync(cancelled, logs)));
        var cancelledEntry = (await WaitForLogs(admin, client.Id, 2)).Items.Single(item => !item.Succeeded);
        var cancelledDetail = await logs.GetAsync(cancelledEntry.Id, CancellationToken.None);
        Assert.Equal("cancelled", cancelledEntry.ErrorCode);
        Assert.Equal("null", cancelledDetail!.RequestParametersJson);
        Assert.Null(cancelledDetail.ResponseBody);

        var brokenLogs = new McpInvocationLogService(new FailingDbFactory(), NullLogger<McpInvocationLogService>.Instance);
        var successful = MakeContext(scope.ServiceProvider, client.ApiKey);
        await middleware.InvokeAsync(successful, brokenLogs);
        Assert.Contains(output, Encoding.UTF8.GetString(((MemoryStream)successful.Response.Body).ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_preserves_historical_rows_and_detail_reports_missing_payloads()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new KaungMcpDbContext(new DbContextOptionsBuilder<KaungMcpDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260923020911_ExpandMcpRequestLogs");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO McpClientApplications (Id,Name,KeyId,KeyHash,IsEnabled,CreatedAtUtc,UpdatedAtUtc)
            VALUES ({id},'legacy','legacy',X'01',1,'2026-09-01','2026-09-01')
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO McpInvocationLogs (ClientApplicationId,ToolName,RequestMethod,StartedAtUtc,DurationMilliseconds,Succeeded)
            VALUES ({id},'kaung_echo','tools/call',0,17,1)
            """);
        await migrator.MigrateAsync();
        var entry = await db.InvocationLogs.SingleAsync();
        Assert.Equal(17, entry.DurationMilliseconds);
        Assert.Null(entry.RequestParametersJson);
        Assert.Null(entry.ResponseBody);

        await using var factory = new McpWebApplicationFactory();
        using var admin = CreateAdmin(factory);
        var client = await CreateClient(admin);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<McpInvocationLogService>();
        await service.WriteAsync(client.Id, "tools/call", "legacy", DateTimeOffset.UtcNow, 17, true, null, null);
        var historical = Assert.Single((await WaitForLogs(admin, client.Id, 1)).Items);
        var detail = await admin.GetFromJsonAsync<InvocationLogDetailResponse>($"/api/admin/call-logs/{historical.Id}");
        Assert.NotNull(detail);
        Assert.Equal("legacy", detail.Log.TargetName);
        Assert.Null(detail.RequestParametersJson);
        Assert.Null(detail.ResponseBody);
    }

    [Theory]
    [InlineData("application/json", "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"isError\":true,\"content\":[]}}", 200, "tool_error")]
    [InlineData("application/json", "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{}}", 200, null)]
    [InlineData("text/plain", "Service unavailable", 503, "request_error")]
    public async Task Json_and_plain_responses_are_saved_verbatim(string contentType, string output, int status, string? errorCode)
    {
        await using var factory = new McpWebApplicationFactory();
        using var admin = CreateAdmin(factory);
        var client = await CreateClient(admin);
        using var scope = factory.Services.CreateScope();
        var logs = scope.ServiceProvider.GetRequiredService<McpInvocationLogService>();
        var context = MakeContext(scope.ServiceProvider, client.ApiKey);
        var middleware = new McpInvocationLoggingMiddleware(async ctx =>
        {
            ctx.Response.ContentType = contentType;
            ctx.Response.StatusCode = status;
            await ctx.Response.WriteAsync(output);
        }, NullLogger<McpInvocationLoggingMiddleware>.Instance);
        await middleware.InvokeAsync(context, logs);
        var entry = Assert.Single((await WaitForLogs(admin, client.Id, 1)).Items);
        var detail = await logs.GetAsync(entry.Id, CancellationToken.None);
        Assert.Equal(output, detail!.ResponseBody);
        Assert.Equal(errorCode, detail.Log.ErrorCode);
        Assert.Equal(errorCode is null, detail.Log.Succeeded);
        Assert.Equal(output, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    private static HttpClient CreateAdmin(McpWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        return client;
    }

    private static async Task<CreatedClientResponse> CreateClient(HttpClient admin) =>
        (await (await admin.PostAsJsonAsync("/api/admin/clients", new CreateClientRequest("details", null)))
            .Content.ReadFromJsonAsync<CreatedClientResponse>())!;

    private static async Task<HttpResponseMessage> Send(HttpClient http, string key, string body, string? protocolVersion = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.ParseAdd("application/json, text/event-stream");
        if (protocolVersion is not null)
        {
            request.Headers.Add("MCP-Protocol-Version", protocolVersion);
            using var document = JsonDocument.Parse(body);
            request.Headers.Add("Mcp-Method", document.RootElement.GetProperty("method").GetString());
        }
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private static Task<PagedResponse<InvocationLogResponse>> WaitForLogs(HttpClient admin, Guid client, int count) =>
        McpWebApplicationFactory.WaitForLogsAsync(admin, client, items => items.Count >= count);

    private static DefaultHttpContext MakeContext(IServiceProvider services, string key)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/mcp";
        context.Request.Method = "POST";
        context.Request.Headers.Authorization = $"Bearer {key}";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"method":"ping"}"""));
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class FailingDbFactory : IDbContextFactory<KaungMcpDbContext>
    {
        public KaungMcpDbContext CreateDbContext() => throw new InvalidOperationException("Database unavailable");
    }
}
