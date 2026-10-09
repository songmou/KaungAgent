namespace KaungMcp.Web;

public interface IWebSearchProvider
{
    Task<WebSearchResponse> SearchAsync(WebSearchRequest request, CancellationToken cancellationToken);
    Task<bool> IsHealthyAsync(CancellationToken cancellationToken);
}

public interface IWebSearchHealthService
{
    Task<WebSearchHealth> GetHealthAsync(CancellationToken cancellationToken);
}

public interface IWebPageService
{
    Task<WebFetchResponse> FetchAsync(
        string url,
        int maxCharacters,
        int startIndex,
        CancellationToken cancellationToken);
}

public interface IBrowserPageRenderer
{
    Task<string?> RenderAsync(Uri uri, CancellationToken cancellationToken);
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

public interface IRobotsPolicy
{
    Task EnsureAllowedAsync(Uri uri, CancellationToken cancellationToken);
}
