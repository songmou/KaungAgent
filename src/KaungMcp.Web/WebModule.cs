using System.Net;
using KaungMcp.Plugin;
using KaungMcp.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
namespace KaungMcp.Web;

public static class WebModule
{
    public static IServiceCollection AddWebModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebSearchOptions>()
            .Bind(configuration.GetSection(WebSearchOptions.SectionName))
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60, "WebSearch timeout must be between 1 and 60 seconds.")
            .Validate(options => Uri.TryCreate(options.SearXng.BaseUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https", "The SearXNG base URL must be an absolute HTTP or HTTPS URL.")
            .Validate(options => options.StaticFallback.TimeoutSeconds is >= 1 and <= 60,
                "Static search timeout must be between 1 and 60 seconds.")
            .Validate(options => options.StaticFallback.MaxResponseBytes is >= 65_536 and <= 5_242_880,
                "Static search response limit is invalid.")
            .Validate(options => Uri.TryCreate(options.StaticFallback.BaseUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo),
                "The static search fallback URL must be an absolute HTTPS URL without user information.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.StaticFallback.UserAgent),
                "Static search fallback user agent is required.")
            ;
        services.AddSingleton<IBrowserPageRenderer, PlaywrightPageRenderer>();
        services.AddSingleton<IWebPageService, WebPageService>();
        services.AddSingleton<SearXngSearchProvider>();
        services.AddSingleton<BingHtmlSearchProvider>();
        services.AddSingleton<FallbackWebSearchProvider>();
        services.AddSingleton<IWebSearchProvider>(services =>
            services.GetRequiredService<FallbackWebSearchProvider>());
        services.AddSingleton<IWebSearchHealthService>(services =>
            services.GetRequiredService<FallbackWebSearchProvider>());
        services.AddHttpClient("SearXng")
            .ConfigureHttpClient((services, client) =>
            {
                var configured = services.GetRequiredService<ManagedSettingsStore>().SearchOptions;
                client.Timeout = TimeSpan.FromSeconds(configured.TimeoutSeconds);
            });
        services.AddHttpClient("StaticSearch")
            .ConfigureHttpClient((services, client) =>
            {
                var configured = services.GetRequiredService<ManagedSettingsStore>().SearchOptions;
                client.Timeout = TimeSpan.FromSeconds(configured.StaticFallback.TimeoutSeconds);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(configured.StaticFallback.UserAgent);
                client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
                UseCookies = false
            });

        services.AddSingleton<WebContentExtractor>();
        return services;
    }
}
public static class WebSettings
{
    public static IReadOnlyList<PluginSettingDefinition> Definitions { get; } =
    [
        new("WebSearch:TimeoutSeconds", "搜索超时", "10", true),
        new("WebSearch:SearXng:Enabled", "启用 SearXNG", "true", true),
        new("WebSearch:SearXng:BaseUrl", "SearXNG 地址", "http://127.0.0.1:8080", true),
        new("WebSearch:StaticFallback:Enabled", "启用静态搜索回退", "true", true),
        new("WebSearch:StaticFallback:BaseUrl", "静态搜索地址", "https://www.bing.com/search", true),
        new("WebSearch:StaticFallback:TimeoutSeconds", "静态搜索超时", "12", true),
        new("WebSearch:StaticFallback:MaxResponseBytes", "静态搜索响应上限", "2097152", true),
        new("WebSearch:StaticFallback:UserAgent", "静态搜索 User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/151 Safari/537.36", true),
        new("WebFetch:MaxResponseBytes", "读取响应上限", "2097152", true),
        new("WebFetch:BrowserFallbackEnabled", "启用浏览器回退", "true", true),
    ];
}
