namespace KaungMcp.Web;

public sealed record WebSearchRequest(
    string Query,
    int MaxResults,
    int Page,
    string Language,
    string? TimeRange,
    string SafeSearch);

public sealed record WebSearchResult(
    int Rank,
    string Title,
    string Url,
    string Snippet,
    string Source,
    IReadOnlyList<string> Engines,
    double? Score,
    DateTimeOffset? PublishedAt);

public sealed record WebSearchResponse(
    string Query,
    string Provider,
    int Page,
    int ReturnedCount,
    IReadOnlyList<WebSearchResult> Results,
    IReadOnlyList<string> Suggestions,
    IReadOnlyList<string> Warnings);

public sealed record WebSearchHealth(
    bool Ready,
    string SearXng,
    string StaticFallback,
    string? ActiveProvider);

public sealed record ExtractedPage(
    string FinalUrl,
    string? Title,
    string? Author,
    string? SiteName,
    string? Language,
    DateTimeOffset? PublishedAt,
    string ContentType,
    string Markdown,
    string RenderedWith);

public sealed record WebFetchResponse(
    string Url,
    string FinalUrl,
    string? Title,
    string? Author,
    string? SiteName,
    string? Language,
    DateTimeOffset? PublishedAt,
    string ContentType,
    string ContentFormat,
    string Content,
    string RenderedWith,
    int StartIndex,
    int ReturnedCharacters,
    int TotalCharacters,
    bool Truncated,
    int? NextStartIndex,
    string Notice);
