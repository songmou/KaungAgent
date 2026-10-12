using System.Net;
using System.Net.Http.Json;
using System.Text;
using KaungMcp.Clients;
using KaungMcp.InvocationLogs;
using KaungMcp.Security;
using KaungMcp.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;

namespace KaungMcp.Tests;

public sealed class WebFeatureTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("169.254.10.2")]
    [InlineData("172.16.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    public void Private_and_reserved_addresses_are_rejected(string address)
    {
        Assert.False(PublicWebAddressPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void Public_addresses_are_allowed(string address)
    {
        Assert.True(PublicWebAddressPolicy.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public async Task Mixed_public_and_private_dns_answers_are_rejected()
    {
        var policy = new PublicWebAddressPolicy(new StubResolver(
            IPAddress.Parse("1.1.1.1"), IPAddress.Parse("10.0.0.1")));

        await Assert.ThrowsAsync<WebFetchException>(() =>
            policy.ValidateUriAsync(new Uri("https://example.test/page"), default));
    }

    [Fact]
    public async Task SearXng_provider_maps_query_and_normalizes_results()
    {
        Uri? requestedUri = null;
        var handler = new DelegateHandler(request =>
        {
            requestedUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "results": [
                    {"url":"https://example.com/a#fragment","title":"<b>First</b>","content":"A &amp; B","engine":"duckduckgo","score":2.5,"publishedDate":"2026-09-20T00:00:00Z"},
                    {"url":"https://example.com/a","title":"Duplicate","content":"ignored","engine":"bing"},
                    {"url":"javascript:alert(1)","title":"Bad","content":"ignored"}
                  ],
                  "suggestions": ["related search"],
                  "unresponsive_engines": [["brave", "timeout"]]
                }
                """, Encoding.UTF8, "application/json")
            };
        });
        var provider = new SearXngSearchProvider(
            new FixedHttpClientFactory(handler),
            Options.Create(new WebSearchOptions
            {
                SearXng = new SearXngOptions { BaseUrl = "https://search.test/" },
                TimeoutSeconds = 10
            }),
            NullLogger<SearXngSearchProvider>.Instance);

        var response = await provider.SearchAsync(
            new WebSearchRequest("mcp 中文", 5, 2, "zh-CN", "week", "strict"),
            default);

        Assert.NotNull(requestedUri);
        Assert.Contains("q=mcp%20%E4%B8%AD%E6%96%87", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("pageno=2", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("time_range=week", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("safesearch=2", requestedUri.Query, StringComparison.Ordinal);
        var result = Assert.Single(response.Results);
        Assert.Equal("First", result.Title);
        Assert.Equal("A & B", result.Snippet);
        Assert.Equal("https://example.com/a", result.Url);
        Assert.Equal("example.com", result.Source);
        Assert.Single(response.Warnings);
    }

    [Fact]
    public async Task Bing_html_provider_maps_parameters_and_parses_results()
    {
        Uri? requestedUri = null;
        var handler = new DelegateHandler(request =>
        {
            requestedUri = request.RequestUri;
            return HtmlResponse("""
                <html><body><ol id="b_results">
                  <li class="b_algo"><h2><a href="https://www.bing.com/ck/a?x=1&amp;u=a1aHR0cHM6Ly9leGFtcGxlLmNvbS9hI3BhcnQ"><strong>First</strong> result</a></h2><div class="b_caption"><p>A &amp; B</p></div></li>
                  <li class="b_algo"><h2><a href="https://example.com/a">Duplicate</a></h2><div class="b_caption"><p>Ignored</p></div></li>
                  <li class="b_algo"><h2><a href="javascript:alert(1)">Invalid</a></h2></li>
                </ol></body></html>
                """);
        });
        var provider = new BingHtmlSearchProvider(
            new FixedHttpClientFactory(handler),
            StaticFallbackOptions(),
            NullLogger<BingHtmlSearchProvider>.Instance);

        var response = await provider.SearchAsync(
            new WebSearchRequest("mcp 中文", 5, 2, "zh-CN", "week", "strict"),
            default);

        Assert.NotNull(requestedUri);
        Assert.Contains("q=mcp%20%E4%B8%AD%E6%96%87", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("first=11", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("setlang=zh-CN", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("adlt=strict", requestedUri.Query, StringComparison.Ordinal);
        Assert.Contains("filters=ex1%3A%22ez2%22", requestedUri.Query, StringComparison.Ordinal);
        var result = Assert.Single(response.Results);
        Assert.Equal("First result", result.Title);
        Assert.Equal("A & B", result.Snippet);
        Assert.Equal("https://example.com/a", result.Url);
        Assert.Equal("bing-html", response.Provider);
    }

    [Fact]
    public async Task Search_provider_uses_static_html_fallback_when_searxng_fails()
    {
        var handler = new DelegateHandler(request =>
            request.RequestUri?.Host == "search.test"
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : HtmlResponse("""
                    <li class="b_algo"><h2><a href="https://example.com/fallback">Fallback result</a></h2><div class="b_caption"><p>Fallback snippet</p></div></li>
                    """));
        var options = StaticFallbackOptions();
        var factory = new FixedHttpClientFactory(handler);
        var searXng = new SearXngSearchProvider(
            factory,
            options,
            NullLogger<SearXngSearchProvider>.Instance);
        var fallback = new BingHtmlSearchProvider(
            factory,
            options,
            NullLogger<BingHtmlSearchProvider>.Instance);
        var provider = new FallbackWebSearchProvider(
            searXng,
            fallback,
            options,
            NullLogger<FallbackWebSearchProvider>.Instance);

        var response = await provider.SearchAsync(
            new WebSearchRequest("fallback", 5, 1, "all", null, "moderate"),
            default);

        Assert.Equal("bing-html", response.Provider);
        Assert.Single(response.Results);
        Assert.Contains(response.Warnings, warning => warning.Contains("SearXNG unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Robots_policy_honors_disallow_and_allows_missing_file()
    {
        var options = Options.Create(new WebFetchOptions());
        var denyFetcher = new StubFetcher(new SafeHttpResponse(
            HttpStatusCode.OK,
            new Uri("https://example.com/robots.txt"),
            "text/plain",
            "User-agent: *\nDisallow: /private"));
        var policy = new RobotsPolicy(
            denyFetcher,
            new FixedHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            options);

        await policy.EnsureAllowedAsync(new Uri("https://example.com/public"), default);
        await Assert.ThrowsAsync<WebFetchException>(() =>
            policy.EnsureAllowedAsync(new Uri("https://example.com/private/data"), default));

        var allowPolicy = new RobotsPolicy(
            new StubFetcher(new SafeHttpResponse(
                HttpStatusCode.NotFound,
                new Uri("https://example.org/robots.txt"),
                "text/plain",
                "")),
            new FixedHttpClientFactory(new DelegateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            options);
        await allowPolicy.EnsureAllowedAsync(new Uri("https://example.org/anything"), default);
    }

    [Fact]
    public async Task Web_page_service_uses_browser_only_when_static_content_is_unreadable()
    {
        var publicResolver = new StubResolver(IPAddress.Parse("93.184.216.34"));
        var addressPolicy = new PublicWebAddressPolicy(publicResolver);
        var rendered = "<html><head><title>Dynamic page</title></head><body><article><h1>Dynamic page</h1><p>" +
            new string('x', 600) + "</p></article></body></html>";
        var browser = new StubBrowser(rendered);
        var service = new WebPageService(
            addressPolicy,
            new AllowRobots(),
            new StubFetcher(new SafeHttpResponse(
                HttpStatusCode.OK,
                new Uri("https://example.com/app"),
                "text/html",
                "<html><body><div id='app'></div><script>render()</script></body></html>")),
            new WebContentExtractor(),
            browser,
            Options.Create(new WebFetchOptions()));

        var response = await service.FetchAsync("https://example.com/app", 1_000, 0, default);

        Assert.Equal("chromium", response.RenderedWith);
        Assert.Equal(1, browser.CallCount);
        Assert.Contains(new string('x', 100), response.Content, StringComparison.Ordinal);
        Assert.Contains("untrusted", response.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Web_page_service_keeps_meaningful_short_static_content_on_http_path()
    {
        var publicResolver = new StubResolver(IPAddress.Parse("93.184.216.34"));
        var browser = new StubBrowser("<html><body>browser must not run</body></html>");
        var staticHtml = "<html><head><title>Short page</title></head><body><main><h1>Short page</h1><p>" +
            new string('x', 140) + "</p></main></body></html>";
        var service = new WebPageService(
            new PublicWebAddressPolicy(publicResolver),
            new AllowRobots(),
            new StubFetcher(new SafeHttpResponse(
                HttpStatusCode.OK,
                new Uri("https://example.com/short"),
                "text/html",
                staticHtml)),
            new WebContentExtractor(),
            browser,
            Options.Create(new WebFetchOptions()));

        var response = await service.FetchAsync("https://example.com/short", 1_000, 0, default);

        Assert.Equal("http", response.RenderedWith);
        Assert.Equal(0, browser.CallCount);
        Assert.Contains(new string('x', 100), response.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Web_tools_are_discoverable_and_return_structured_content_end_to_end()
    {
        var searchResponse = new WebSearchResponse(
            "mcp", "searxng", 1, 1,
            [new WebSearchResult(1, "MCP", "https://example.com", "Result", "example.com", ["test"], 1, null)],
            [], []);
        var fetchResponse = new WebFetchResponse(
            "https://example.com", "https://example.com", "Example", null, "Example", "en", null,
            "text/html", "markdown", "Example content", "http", 0, 15, 15, false, null, "Untrusted external data");
        await using var factory = new McpWebApplicationFactory(services =>
        {
            services.AddSingleton<IWebSearchProvider>(new StubSearchProvider(searchResponse));
            services.AddSingleton<IWebSearchHealthService>(new StubSearchHealthService());
            services.AddSingleton<IWebPageService>(new StubPageService(fetchResponse));
            services.AddSingleton<IBrowserPageRenderer>(new StubBrowser("<html></html>"));
        });
        using var adminClient = factory.CreateClient();
        adminClient.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpWebApplicationFactory.AdminKey);
        var createdResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/clients",
            new CreateClientRequest("web-test-agent", "Web tool integration test"));
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<CreatedClientResponse>();
        Assert.NotNull(created);

        await using var client = await CreateMcpClientAsync(factory, created.ApiKey);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, tool => tool.Name == "kaung_web_search" && tool.ProtocolTool.OutputSchema is not null);
        Assert.Contains(tools, tool => tool.Name == "kaung_web_fetch" && tool.ProtocolTool.OutputSchema is not null);

        var search = await client.CallToolAsync(
            "kaung_web_search",
            new Dictionary<string, object?> { ["query"] = "mcp" });
        Assert.NotEqual(true, search.IsError);
        Assert.Contains("example.com", search.StructuredContent?.GetRawText(), StringComparison.Ordinal);

        var fetch = await client.CallToolAsync(
            "kaung_web_fetch",
            new Dictionary<string, object?> { ["url"] = "https://example.com" });
        Assert.NotEqual(true, fetch.IsError);
        Assert.Contains("Example content", fetch.StructuredContent?.GetRawText(), StringComparison.Ordinal);

        var invalidFetch = await client.CallToolAsync(
            "kaung_web_fetch",
            new Dictionary<string, object?>
            {
                ["url"] = "https://example.com",
                ["maxCharacters"] = 10
            });
        Assert.True(invalidFetch.IsError);
        Assert.Contains(
            "maxCharacters must be between",
            Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(invalidFetch.Content[0]).Text,
            StringComparison.Ordinal);

        var health = await adminClient.GetAsync("/health/plugins");
        health.EnsureSuccessStatusCode();

        var logs = await McpWebApplicationFactory.WaitForLogsAsync(adminClient, created.Id, items =>
            items.Any(item => item.ToolName == "kaung_web_search" && item.Succeeded) &&
            items.Any(item => item.ToolName == "kaung_web_fetch" && item.Succeeded) &&
            items.Any(item => item.ToolName == "kaung_web_fetch" && !item.Succeeded));
        Assert.NotNull(logs);
        Assert.Contains(logs.Items, item => item.ToolName == "kaung_web_search" && item.Succeeded);
        Assert.Contains(logs.Items, item => item.ToolName == "kaung_web_fetch" && item.Succeeded);
        Assert.Contains(logs.Items, item =>
            item.ToolName == "kaung_web_fetch" && !item.Succeeded && item.ErrorCode == "mcp_error");
    }

    private static async Task<McpClient> CreateMcpClientAsync(McpWebApplicationFactory factory, string apiKey)
    {
        var httpClient = factory.CreateClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" }
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static IOptions<WebSearchOptions> StaticFallbackOptions() =>
        Options.Create(new WebSearchOptions
        {
            SearXng = new SearXngOptions
            {
                Enabled = true,
                BaseUrl = "https://search.test/"
            },
            StaticFallback = new StaticSearchFallbackOptions
            {
                Enabled = true,
                BaseUrl = "https://www.bing.com/search",
                TimeoutSeconds = 10,
                MaxResponseBytes = 2 * 1024 * 1024,
                UserAgent = "KaungMcpTests/1.0"
            }
        });

    private static HttpResponseMessage HtmlResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };

    private sealed class StubResolver(params IPAddress[] addresses) : IHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Task.FromResult(addresses);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class FixedHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubFetcher(SafeHttpResponse response) : ISafeHttpFetcher
    {
        public Task<SafeHttpResponse> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class AllowRobots : IRobotsPolicy
    {
        public Task EnsureAllowedAsync(Uri uri, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StubBrowser(string html) : IBrowserPageRenderer
    {
        public int CallCount { get; private set; }
        public Task<string?> RenderAsync(Uri uri, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<string?>(html);
        }
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class StubSearchProvider(WebSearchResponse response) : IWebSearchProvider
    {
        public Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken) => Task.FromResult(response);
        public Task<bool> IsHealthyAsync(CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class StubSearchHealthService : IWebSearchHealthService
    {
        public Task<WebSearchHealth> GetHealthAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WebSearchHealth(true, "ready", "standby", "searxng"));
    }

    private sealed class StubPageService(WebFetchResponse response) : IWebPageService
    {
        public Task<WebFetchResponse> FetchAsync(string url, int maxCharacters, int startIndex, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
