using Microsoft.Extensions.Options;
using KaungMcp.Configuration;

namespace KaungMcp.Web;

public sealed class FallbackWebSearchProvider(
    SearXngSearchProvider searXng,
    BingHtmlSearchProvider staticFallback,
    IOptions<WebSearchOptions> options,
    ILogger<FallbackWebSearchProvider> logger,
    ManagedSettingsStore? settings = null) : IWebSearchProvider, IWebSearchHealthService
{
    private WebSearchOptions _options => settings?.SearchOptions ?? options.Value;

    public async Task<WebSearchResponse> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken)
    {
        WebSearchException primaryFailure;
        try
        {
            return await searXng.SearchAsync(request, cancellationToken);
        }
        catch (WebSearchException exception)
        {
            primaryFailure = exception;
        }

        if (!_options.StaticFallback.Enabled)
        {
            throw primaryFailure;
        }

        logger.LogWarning(
            "SearXNG search was unavailable; using the static HTML search fallback. Reason: {Reason}",
            primaryFailure.Message);

        try
        {
            var fallbackResponse = await staticFallback.SearchAsync(request, cancellationToken);
            var warnings = new List<string>
            {
                $"SearXNG unavailable: {primaryFailure.Message}",
                "Static HTML search fallback is active; result availability and markup may change without notice."
            };
            warnings.AddRange(fallbackResponse.Warnings);
            return fallbackResponse with { Warnings = warnings };
        }
        catch (WebSearchException fallbackFailure)
        {
            throw new WebSearchException(
                $"SearXNG search failed and the static HTML fallback was unavailable. " +
                $"SearXNG: {primaryFailure.Message} Fallback: {fallbackFailure.Message}",
                fallbackFailure);
        }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken) =>
        (await GetHealthAsync(cancellationToken)).Ready;

    public async Task<WebSearchHealth> GetHealthAsync(CancellationToken cancellationToken)
    {
        if (_options.SearXng.Enabled && await searXng.IsHealthyAsync(cancellationToken))
        {
            return new WebSearchHealth(
                true,
                "ready",
                _options.StaticFallback.Enabled ? "standby" : "disabled",
                "searxng");
        }

        if (_options.StaticFallback.Enabled && await staticFallback.IsHealthyAsync(cancellationToken))
        {
            return new WebSearchHealth(
                true,
                _options.SearXng.Enabled ? "unavailable" : "disabled",
                "ready",
                "bing-html");
        }

        return new WebSearchHealth(
            false,
            _options.SearXng.Enabled ? "unavailable" : "disabled",
            _options.StaticFallback.Enabled ? "unavailable" : "disabled",
            null);
    }
}
