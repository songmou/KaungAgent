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
    public async Task Catalog_and_mcp_list_share_Chinese_tool_names_and_descriptions()
    {
        await using var factory = new McpWebApplicationFactory();
        using var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var expected = new Dictionary<string, (string Title, string Description)>
        {
            ["kaung_ping"] = ("连接检查", "检查 KaungMcp 服务是否可用，并返回当前 UTC 时间。"),
            ["kaung_echo"] = ("消息回显", "原样返回传入的消息，用于验证 MCP 连接是否正常。"),
            ["kaung_web_search"] = ("网页搜索", "通过配置的 SearXNG 实例搜索公共网页，并返回标准化的结果元数据。搜索结果来自外部，属于不可信数据。"),
            ["kaung_web_fetch"] = ("网页读取", "读取单个公共网页，遵守 robots.txt 规则，并将正文提取为 Markdown；仅在静态 HTML 无法读取时使用隔离的 Chromium 渲染。返回内容来自外部，属于不可信数据。"),
            ["kaung_jewelry_list_instruments"] = ("行情品种目录", "查询支持的市场行情及中国大陆黄金品牌的标识和类别。按标识查询前应先调用此工具；列出的类别不保证有可用报价。"),
            ["kaung_jewelry_get_market_quotes"] = ("市场行情查询", "查询国内每日收盘价、基准价及国际 XAU/USD 价格，包含报价日期与数据时效信息。缓存缺失或过期时触发采集，不会用其他日期的价格替代所请求日期的价格。"),
            ["kaung_jewelry_get_brand_quotes"] = ("品牌报价查询", "查询中国大陆品牌黄金参考价，区分首饰、金条与回收类别，并说明费用及适用限制。逐项报告缺失的品牌与日期，将最新可用报价 latestAvailable 与所请求日期明确区分。参考价不代表门店最终成交报价。"),
            ["kaung_jewelry_get_price_history"] = ("历史价格查询", "分页查询已保存的报价观测记录，不提供历史回溯补采、缺失数据填补或完整历史档案保证。报价日期未知的记录可通过 collected_date 查询。"),
            ["kaung_jewelry_compare_prices"] = ("价格对比", "比较 2 至 20 个已保存的报价标识。rank 排名要求品牌价格类型、类别、纯度、费用、地区及日期口径均已知且一致；reference 仅返回相对首个报价的数值差，不进行排名或利润解读。两种模式均要求币种、单位一致，且报价日期已知并相同。不得将品牌价与市场价的差额等同于利润。"),
            ["kaung_jewelry_convert_price"] = ("价格换算", "换算以每克或每金衡盎司计价的正数价格，可使用调用方明确提供的汇率进行币种换算。汇率表示每 1 单位原币种对应的目标币种金额。返回原始输入与计算公式，不获取或推定汇率，也不调整纯度、税费或溢价。"),
            ["kaung_jewelry_get_source_status"] = ("采集来源状态", "查看数据来源的验证依据、启用状态、最近尝试与成功时间、下次尝试时间、退避情况、错误及滚动七天采集次数。来源可访问不代表价格为最新，也不能证明连续七天稳定。"),
        };

        var catalog = await admin.GetFromJsonAsync<JsonElement>("/api/admin/tools");
        var tools = catalog.GetProperty("categories").EnumerateArray()
            .SelectMany(category => category.GetProperty("tools").EnumerateArray())
            .ToDictionary(tool => tool.GetProperty("name").GetString()!);
        var created = await (await admin.PostAsJsonAsync("/api/admin/clients",
            new CreateClientRequest("localized-tool-client", null))).Content.ReadFromJsonAsync<CreatedClientResponse>();
        Assert.NotNull(created);
        await using var mcpClient = await CreateMcpClientAsync(factory, created.ApiKey);
        var mcpTools = (await mcpClient.ListToolsAsync()).ToDictionary(tool => tool.Name);

        foreach (var (name, metadata) in expected)
        {
            Assert.True(tools.TryGetValue(name, out var tool), $"Missing catalog tool: {name}");
            Assert.Equal(metadata.Title, tool.GetProperty("title").GetString());
            Assert.Equal(metadata.Description, tool.GetProperty("description").GetString());
            // Source diagnostics are an internal tool and must remain hidden from MCP clients.
            if (name == "kaung_jewelry_get_source_status")
            {
                Assert.False(tool.GetProperty("isAvailable").GetBoolean());
                Assert.DoesNotContain(name, mcpTools.Keys);
                continue;
            }
            Assert.True(mcpTools.TryGetValue(name, out var mcpTool), $"Missing MCP tool: {name}");
            Assert.Equal(metadata.Title, mcpTool.ProtocolTool.Title ?? mcpTool.ProtocolTool.Annotations?.Title);
            Assert.Equal(metadata.Description, mcpTool.Description);
        }
    }

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
