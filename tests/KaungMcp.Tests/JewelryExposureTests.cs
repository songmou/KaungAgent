using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using KaungMcp.Clients;
using KaungMcp.Jewelry;
using KaungMcp.Security;
using KaungMcp.Tools;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace KaungMcp.Tests;

public sealed class JewelryExposureTests
{
    [Fact]
    public async Task Pure_data_host_hides_other_capabilities_and_never_reenables_internal_status_for_clients()
    {
        await using var factory = new McpWebApplicationFactory(services =>
            services.Configure<JewelryOptions>(o => o.Enabled = false), jewelryOnly: true);
        using var admin = factory.CreateClient();
        var unauthorized = await admin.PostAsJsonAsync("/mcp", new { jsonrpc = "2.0", id = 1, method = "tools/list" });
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        admin.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var created = await (await admin.PostAsJsonAsync("/api/admin/clients", new CreateClientRequest("data-tests", null)))
            .Content.ReadFromJsonAsync<CreatedClientResponse>();
        using var scope = factory.Services.CreateScope();
        var management = scope.ServiceProvider.GetRequiredService<McpToolManagementService>();
        await management.SetEnabledAsync("kaung_jewelry_get_source_status", true, default);
        var internalStatus = await management.TestAsync("kaung_jewelry_get_source_status", JsonSerializer.SerializeToElement(new { }), false, default);
        Assert.True(internalStatus!.Succeeded);
        Assert.Contains("source", internalStatus.Result.StructuredContent!.Value.GetRawText(), StringComparison.OrdinalIgnoreCase);
        var http = factory.CreateClient();
        await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + created!.ApiKey }
        }, http, ownsHttpClient: true));
        var tools = await mcp.ListToolsAsync();
        Assert.Equal(6, tools.Count);
        Assert.All(tools, t => Assert.StartsWith("kaung_jewelry_", t.Name));
        Assert.Null(mcp.ServerCapabilities.Prompts);
        Assert.Null(mcp.ServerCapabilities.Resources);
        foreach (var name in new[] { "kaung_echo", "kaung_jewelry_get_source_status" })
        {
            var blocked = await mcp.CallToolAsync(name, new Dictionary<string, object?>());
            Assert.True(blocked.IsError);
        }
        var brand = tools.Single(t => t.Name == "kaung_jewelry_get_brand_quotes");
        Assert.False(brand.ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("sourceId", out _));
        var quotes = await mcp.CallToolAsync(brand.Name, new Dictionary<string, object?> { ["date"] = "today" });
        var json = quotes.StructuredContent!.Value.GetRawText();
        Assert.DoesNotContain("sourceId", json);
        Assert.DoesNotContain("sourceUrl", json);
        Assert.DoesNotContain("LastError", json);
        var conversion = await mcp.CallToolAsync("kaung_jewelry_convert_price", new Dictionary<string, object?>
        {
            ["price"] = 100, ["fromCurrency"] = "USD", ["fromUnit"] = "gram", ["toCurrency"] = "CNY", ["toUnit"] = "gram",
            ["exchangeRate"] = 7, ["exchangeRateSource"] = "https://secret-fx.test", ["exchangeRateTimestamp"] = "2026-10-09T04:00:00Z"
        });
        Assert.NotEqual(true, conversion.IsError);
        Assert.DoesNotContain("secret-fx", conversion.StructuredContent!.Value.GetRawText());
        Assert.False(conversion.StructuredContent.Value.TryGetProperty("exchangeRateSource", out _));
    }
}
