using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using KaungMcp.Configuration;

namespace KaungMcp.Web;

public sealed partial class SearXngSearchProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<WebSearchOptions> options,
    ILogger<SearXngSearchProvider> logger,
    ManagedSettingsStore? settings = null) : IWebSearchProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private WebSearchOptions _options => settings?.SearchOptions ?? options.Value;

    public async Task<WebSearchResponse> SearchAsync(
        WebSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (!_options.SearXng.Enabled)
        {
            throw new WebSearchException("SearXNG is disabled.");
        }

        var uri = BuildSearchUri(request);
        using var response = await SendWithOneRetryAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new WebSearchException(
                $"SearXNG search failed with HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        SearXngResponse? payload;
        try
        {
            payload = await response.Content.ReadFromJsonAsync<SearXngResponse>(JsonOptions, cancellationToken);
        }
        catch (JsonException exception)
        {
            throw new WebSearchException("SearXNG returned an invalid JSON response.", exception);
        }

        if (payload is null)
        {
            throw new WebSearchException("SearXNG returned an empty response.");
        }

        var results = new List<WebSearchResult>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in payload.Results ?? [])
        {
            if (results.Count >= request.MaxResults ||
                !TryNormalizePublicUrl(item.Url, out var normalizedUrl) ||
                !seenUrls.Add(normalizedUrl))
            {
                continue;
            }

            var engines = item.Engines is { Count: > 0 }
                ? item.Engines.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : string.IsNullOrWhiteSpace(item.Engine) ? [] : [item.Engine.Trim()];
            var publishedAt = DateTimeOffset.TryParse(item.PublishedDate, out var parsedDate)
                ? parsedDate
                : (DateTimeOffset?)null;

            results.Add(new WebSearchResult(
                results.Count + 1,
                CleanText(item.Title),
                normalizedUrl,
                CleanText(item.Content),
                new Uri(normalizedUrl).Host,
                engines,
                item.Score,
                publishedAt));
        }

        var suggestions = (payload.Suggestions ?? [])
            .Select(GetJsonText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var warnings = (payload.UnresponsiveEngines ?? [])
            .Select(GetWarningText)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();

        return new WebSearchResponse(
            request.Query,
            "searxng",
            request.Page,
            results.Count,
            results,
            suggestions,
            warnings);
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken)
    {
        if (!_options.SearXng.Enabled)
        {
            return false;
        }

        try
        {
            var request = new WebSearchRequest("kaung health check", 1, 1, "all", null, "moderate");
            using var response = await httpClientFactory.CreateClient("SearXng")
                .GetAsync(BuildSearchUri(request), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or UriFormatException)
        {
            logger.LogDebug(exception, "SearXNG health check failed.");
            return false;
        }
    }

    private Uri BuildSearchUri(WebSearchRequest request)
    {
        if (!Uri.TryCreate(_options.SearXng.BaseUrl, UriKind.Absolute, out var baseUri) ||
            (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebSearchException("The configured SearXNG base URL is invalid.");
        }

        var normalizedBase = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/");
        var endpoint = new Uri(normalizedBase, "search").AbsoluteUri;
        var query = new Dictionary<string, string?>
        {
            ["q"] = request.Query,
            ["format"] = "json",
            ["categories"] = "general",
            ["pageno"] = request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["language"] = request.Language,
            ["safesearch"] = request.SafeSearch switch
            {
                "off" => "0",
                "strict" => "2",
                _ => "1"
            },
            ["time_range"] = request.TimeRange
        };
        return new Uri(QueryHelpers.AddQueryString(endpoint, query));
    }

    private async Task<HttpResponseMessage> SendWithOneRetryAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var response = await httpClientFactory.CreateClient("SearXng")
                    .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (attempt == 0 && IsTransient(response.StatusCode))
                {
                    var retryAfter = response.Headers.RetryAfter;
                    var delay = retryAfter?.Delta ??
                        (retryAfter?.Date is { } date
                            ? date - DateTimeOffset.UtcNow
                            : TimeSpan.FromMilliseconds(250));
                    delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
                    var maximumDelay = TimeSpan.FromSeconds(_options.TimeoutSeconds);
                    response.Dispose();
                    await Task.Delay(delay > maximumDelay ? maximumDelay : delay, cancellationToken);
                    continue;
                }

                return response;
            }
            catch (HttpRequestException exception) when (attempt == 0)
            {
                logger.LogDebug(exception, "Transient SearXNG request failure; retrying once.");
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WebSearchException("SearXNG search timed out.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new WebSearchException("Unable to connect to SearXNG.", exception);
            }
        }

        throw new WebSearchException("Unable to connect to SearXNG after retrying.");
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.RequestTimeout ||
        statusCode == HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    private static bool TryNormalizePublicUrl(string? value, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
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

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var withoutTags = HtmlTagRegex().Replace(value, " ");
        return WhitespaceRegex().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static string? GetJsonText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => CleanText(element.GetString()),
        JsonValueKind.Object when element.TryGetProperty("text", out var text) => CleanText(text.GetString()),
        _ => null
    };

    private static string? GetWarningText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return string.Join(": ", element.EnumerateArray().Select(GetJsonText).Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        return GetJsonText(element) ?? CleanText(element.ToString());
    }

    [GeneratedRegex("<[^>]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();

    private sealed record SearXngResponse(
        IReadOnlyList<SearXngResult>? Results,
        IReadOnlyList<JsonElement>? Suggestions,
        [property: JsonPropertyName("unresponsive_engines")] IReadOnlyList<JsonElement>? UnresponsiveEngines)
    {
        public SearXngResponse() : this([], [], []) { }
    }

    private sealed record SearXngResult(
        string? Url,
        string? Title,
        string? Content,
        string? Engine,
        IReadOnlyList<string>? Engines,
        double? Score,
        string? PublishedDate)
    {
        public SearXngResult() : this(null, null, null, null, [], null, null) { }
    }
}

public sealed class WebSearchException : Exception
{
    public WebSearchException(string message) : base(message) { }
    public WebSearchException(string message, Exception innerException) : base(message, innerException) { }
}
