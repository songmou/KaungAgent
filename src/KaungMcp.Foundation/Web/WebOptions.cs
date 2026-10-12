namespace KaungMcp.Web;

public sealed class WebSearchOptions
{
    public const string SectionName = "WebSearch";

    public SearXngOptions SearXng { get; init; } = new();
    public StaticSearchFallbackOptions StaticFallback { get; init; } = new();
    public int TimeoutSeconds { get; init; } = 10;
}

public sealed class SearXngOptions
{
    public bool Enabled { get; init; } = true;
    public string BaseUrl { get; init; } = "http://127.0.0.1:8080";
}

public sealed class StaticSearchFallbackOptions
{
    public bool Enabled { get; init; } = true;
    public string BaseUrl { get; init; } = "https://www.bing.com/search";
    public int TimeoutSeconds { get; init; } = 12;
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
    public string UserAgent { get; init; } =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 Chrome/151 Safari/537.36";
}

public sealed class WebFetchOptions
{
    public const string SectionName = "WebFetch";

    public int TimeoutSeconds { get; init; } = 15;
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxRedirects { get; init; } = 5;
    public bool BrowserFallbackEnabled { get; init; } = true;
    public string UserAgent { get; init; } = "KaungMcpBot/1.0 (Autonomous web fetcher)";
    public int RobotsCacheMinutes { get; init; } = 30;
}
