using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using KaungMcp.Configuration;

namespace KaungMcp.Web;

public sealed partial class BingHtmlSearchProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<WebSearchOptions> options,
    ILogger<BingHtmlSearchProvider> logger,
    ManagedSettingsStore? settings = null)
{
    private WebSearchOptions _options => settings?.SearchOptions ?? options.Value;

    public async Task<WebSearchResponse> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.StaticFallback.Enabled)
        {
            throw new WebSearchException("The static HTML search fallback is disabled.");
        }

        var uri = BuildSearchUri(request);
        using var response = await SendWithOneRetryAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new WebSearchException(
                $"Static HTML search failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        if (response.Content.Headers.ContentLength is { } length &&
            length > _options.StaticFallback.MaxResponseBytes)
        {
            throw new WebSearchException("Static HTML search returned an oversized response.");
        }

        string html;
        try
        {
            await response.Content.LoadIntoBufferAsync(
                _options.StaticFallback.MaxResponseBytes,
                cancellationToken);
            html = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new WebSearchException("Static HTML search returned an oversized or unreadable response.", exception);
        }

        if (ChallengeRegex().IsMatch(html))
        {
            throw new WebSearchException("Static HTML search was blocked by a verification or rate-limit page.");
        }

        var results = ParseResults(html, request.MaxResults);
        return new WebSearchResponse(
            request.Query,
            "bing-html",
            request.Page,
            results.Count,
            results,
            [],
            []);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await SearchAsync(
                new WebSearchRequest("kaung mcp", 1, 1, "all", null, "moderate"),
                cancellationToken);
            return response.Results.Count > 0;
        }
        catch (Exception exception) when (exception is WebSearchException or OperationCanceledException)
        {
            logger.LogDebug(exception, "Static HTML search health check failed.");
            return false;
        }
    }

    internal IReadOnlyList<WebSearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<WebSearchResult>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match blockMatch in ResultBlockRegex().Matches(html))
        {
            var block = blockMatch.Groups["body"].Value;
            var titleMatch = ResultTitleRegex().Match(block);
            if (!titleMatch.Success ||
                !TryNormalizeResultUrl(titleMatch.Groups["url"].Value, out var normalizedUrl) ||
                !seenUrls.Add(normalizedUrl))
            {
                continue;
            }

            var snippetMatch = ResultSnippetRegex().Match(block);
            results.Add(new WebSearchResult(
                results.Count + 1,
                CleanText(titleMatch.Groups["title"].Value),
                normalizedUrl,
                snippetMatch.Success ? CleanText(snippetMatch.Groups["snippet"].Value) : "",
                new Uri(normalizedUrl).Host,
                ["bing-html"],
                null,
                null));

            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    internal Uri BuildSearchUri(WebSearchRequest request)
    {
        if (!Uri.TryCreate(_options.StaticFallback.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new WebSearchException("The static HTML search fallback URL is invalid.");
        }

        var query = new Dictionary<string, string?>
        {
            ["q"] = request.Query,
            ["first"] = (((request.Page - 1) * 10) + 1).ToString(CultureInfo.InvariantCulture),
            ["count"] = "10",
            ["setlang"] = request.Language.Equals("all", StringComparison.OrdinalIgnoreCase)
                ? null
                : request.Language,
            ["adlt"] = request.SafeSearch switch
            {
                "off" => "off",
                "strict" => "strict",
                _ => "moderate"
            },
            ["filters"] = request.TimeRange switch
            {
                "day" => "ex1:\"ez1\"",
                "week" => "ex1:\"ez2\"",
                "month" => "ex1:\"ez3\"",
                "year" => "ex1:\"ez5\"",
                _ => null
            }
        };
        return new Uri(QueryHelpers.AddQueryString(baseUri.AbsoluteUri, query));
    }

    private async Task<HttpResponseMessage> SendWithOneRetryAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var response = await httpClientFactory.CreateClient("StaticSearch")
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (attempt == 0 && IsTransient(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException exception) when (attempt == 0)
            {
                logger.LogDebug(exception, "Static HTML search request failed; retrying once.");
                await Task.Delay(TimeSpan.FromMilliseconds(300), cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WebSearchException("Static HTML search timed out.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new WebSearchException("Unable to connect to the static HTML search provider.", exception);
            }
        }

        throw new WebSearchException("Unable to connect to the static HTML search provider after retrying.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool TryNormalizeResultUrl(string value, out string normalized)
    {
        normalized = "";
        var decoded = UnwrapBingRedirect(WebUtility.HtmlDecode(value).Trim());
        if (!Uri.TryCreate(decoded, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        var builder = new UriBuilder(uri) { Fragment = "" };
        if ((builder.Scheme == Uri.UriSchemeHttp && builder.Port == 80) ||
            (builder.Scheme == Uri.UriSchemeHttps && builder.Port == 443))
        {
            builder.Port = -1;
        }

        normalized = builder.Uri.AbsoluteUri;
        return true;
    }

    private static string UnwrapBingRedirect(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var redirectUri) ||
            !redirectUri.Host.EndsWith(".bing.com", StringComparison.OrdinalIgnoreCase) &&
            !redirectUri.Host.Equals("bing.com", StringComparison.OrdinalIgnoreCase) ||
            !redirectUri.AbsolutePath.StartsWith("/ck/", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var encodedTarget = QueryHelpers.ParseQuery(redirectUri.Query)["u"].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(encodedTarget))
        {
            return value;
        }

        try
        {
            var target = WebUtility.UrlDecode(encodedTarget);
            if (target.StartsWith("a1", StringComparison.Ordinal))
            {
                target = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(target[2..]));
            }

            return Uri.TryCreate(target, UriKind.Absolute, out var targetUri) &&
                   (targetUri.Scheme == Uri.UriSchemeHttp || targetUri.Scheme == Uri.UriSchemeHttps)
                ? target
                : value;
        }
        catch (FormatException)
        {
            return value;
        }
    }

    private static string CleanText(string value)
    {
        var withoutTags = HtmlTagRegex().Replace(value, " ");
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    [GeneratedRegex("""<li\b(?=[^>]*\bclass\s*=\s*["'][^"']*\bb_algo\b[^"']*["'])[^>]*>(?<body>.*?)</li>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 1_000)]
    private static partial Regex ResultBlockRegex();

    [GeneratedRegex("""<h2\b[^>]*>\s*<a\b[^>]*\bhref\s*=\s*["'](?<url>[^"']+)["'][^>]*>(?<title>.*?)</a>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 1_000)]
    private static partial Regex ResultTitleRegex();

    [GeneratedRegex("""<div\b(?=[^>]*\bclass\s*=\s*["'][^"']*\bb_caption\b[^"']*["'])[^>]*>.*?<p\b[^>]*>(?<snippet>.*?)</p>""", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 1_000)]
    private static partial Regex ResultSnippetRegex();

    [GeneratedRegex("""captcha|unusual traffic|verify you are human|id=["']b_captcha["']""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1_000)]
    private static partial Regex ChallengeRegex();

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant, 1_000)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant, 1_000)]
    private static partial Regex WhitespaceRegex();
}
