using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using KaungMcp.Configuration;
using Robots.Txt.Parser;
using Robots.Txt.Parser.Http;

namespace KaungMcp.Web;

public sealed class RobotsPolicy(
    ISafeHttpFetcher fetcher,
    IHttpClientFactory httpClientFactory,
    IOptions<WebFetchOptions> options,
    ManagedSettingsStore? settings = null) : IRobotsPolicy
{
    private const int MaximumRobotsBytes = 512 * 1024;
    private static readonly ProductToken Product = ProductToken.Parse("KaungMcpBot");
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private WebFetchOptions _options => settings?.FetchOptions ?? options.Value;
    private long _observedSettingsVersion;

    public async Task EnsureAllowedAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (settings is not null && settings.Version != Interlocked.Read(ref _observedSettingsVersion))
        {
            _cache.Clear();
            Interlocked.Exchange(ref _observedSettingsVersion, settings.Version);
        }
        var origin = uri.GetLeftPart(UriPartial.Authority);
        if (!_cache.TryGetValue(origin, out var cached) || cached.ExpiresAtUtc <= DateTimeOffset.UtcNow)
        {
            cached = await LoadAsync(uri, cancellationToken);
            _cache[origin] = cached;
        }

        if (cached.AllowAll)
        {
            return;
        }

        if (cached.Robots is null ||
            (cached.Robots.TryGetRules(Product, out var rules) && !rules.IsAllowed(uri.PathAndQuery)))
        {
            throw new WebFetchException($"The site's robots.txt does not allow autonomous access to '{uri.PathAndQuery}'.");
        }
    }

    private async Task<CacheEntry> LoadAsync(Uri target, CancellationToken cancellationToken)
    {
        var robotsUri = new UriBuilder(target.Scheme, target.Host, target.Port, "/robots.txt").Uri;
        SafeHttpResponse response;
        try
        {
            response = await fetcher.FetchAsync(robotsUri, MaximumRobotsBytes, cancellationToken);
        }
        catch (WebFetchException exception)
        {
            throw new WebFetchException("Unable to verify the site's robots.txt; autonomous access was denied.", exception);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new CacheEntry(null, true, ExpiresAt());
        }

        if (!IsSuccess(response.StatusCode))
        {
            throw new WebFetchException(
                $"The site's robots.txt returned HTTP {(int)response.StatusCode}; autonomous access was denied.");
        }

        try
        {
            await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(response.Text));
            var parser = new RobotsTxtParser(
                new RobotWebClient(httpClientFactory.CreateClient("WebFetch")),
                robotsUri);
            var robots = await parser.ReadFromStreamAsync(stream, cancellationToken);
            return new CacheEntry(robots, false, ExpiresAt());
        }
        catch (Exception exception) when (exception is RobotsTxtException or InvalidOperationException)
        {
            throw new WebFetchException("The site's robots.txt could not be parsed; autonomous access was denied.", exception);
        }
    }

    private DateTimeOffset ExpiresAt() =>
        DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.RobotsCacheMinutes));

    private static bool IsSuccess(HttpStatusCode statusCode) => (int)statusCode is >= 200 and <= 299;

    private sealed record CacheEntry(IRobotsTxt? Robots, bool AllowAll, DateTimeOffset ExpiresAtUtc);
}
