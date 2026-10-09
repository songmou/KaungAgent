using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using KaungMcp.Configuration;

namespace KaungMcp.Web;

public sealed record SafeHttpResponse(
    HttpStatusCode StatusCode,
    Uri FinalUri,
    string? ContentType,
    string Text);

public interface ISafeHttpFetcher
{
    Task<SafeHttpResponse> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken);
    Task<SafeHttpResponse> FetchWithHeadersAsync(Uri uri, int maxBytes,
        IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken) =>
        FetchAsync(uri, maxBytes, cancellationToken);
}

public sealed class SafeHttpFetcher(
    IHttpClientFactory httpClientFactory,
    PublicWebAddressPolicy addressPolicy,
    IOptions<WebFetchOptions> options,
    ManagedSettingsStore? settings = null) : ISafeHttpFetcher
{
    private WebFetchOptions _options => settings?.FetchOptions ?? options.Value;

    public async Task<SafeHttpResponse> FetchAsync(
        Uri uri,
        int maxBytes,
        CancellationToken cancellationToken)
        => await FetchWithHeadersAsync(uri, maxBytes, new Dictionary<string, string>(), cancellationToken);

    public async Task<SafeHttpResponse> FetchWithHeadersAsync(Uri uri, int maxBytes,
        IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var current = uri;
        for (var redirect = 0; redirect <= _options.MaxRedirects; redirect++)
        {
            await addressPolicy.ValidateUriAsync(current, cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.UserAgent.ParseAdd(_options.UserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,text/plain;q=0.9,*/*;q=0.1");
            // Only forward public adapter headers to the original origin, never to a redirect host.
            if (current.GetLeftPart(UriPartial.Authority) == uri.GetLeftPart(UriPartial.Authority))
                foreach (var header in headers) request.Headers.Add(header.Key, header.Value);

            HttpResponseMessage response;
            try
            {
                response = await httpClientFactory.CreateClient("WebFetch")
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new WebFetchException("The web request timed out.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw new WebFetchException("Unable to retrieve the requested URL.", exception);
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    if (redirect == _options.MaxRedirects)
                    {
                        throw new WebFetchException($"The URL exceeded the {_options.MaxRedirects}-redirect limit.");
                    }

                    var location = response.Headers.Location
                        ?? throw new WebFetchException("The server returned a redirect without a Location header.");
                    current = location.IsAbsoluteUri ? location : new Uri(current, location);
                    continue;
                }

                if (response.Content.Headers.ContentLength is { } declaredLength && declaredLength > maxBytes)
                {
                    throw new WebFetchException($"The response exceeds the {maxBytes}-byte download limit.");
                }

                var bytes = await ReadLimitedAsync(response.Content, maxBytes, cancellationToken);
                var encoding = ResolveEncoding(response.Content.Headers.ContentType?.CharSet);
                return new SafeHttpResponse(
                    response.StatusCode,
                    response.RequestMessage?.RequestUri ?? current,
                    response.Content.Headers.ContentType?.MediaType,
                    encoding.GetString(bytes));
            }
        }

        throw new WebFetchException("The redirect limit was exceeded.");
    }

    private static async Task<byte[]> ReadLimitedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return output.ToArray();
            }

            if (output.Length + read > maxBytes)
            {
                throw new WebFetchException($"The response exceeds the {maxBytes}-byte download limit.");
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                return Encoding.GetEncoding(charset.Trim('"', '\''));
            }
            catch (ArgumentException)
            {
                // Invalid charsets fall back to UTF-8.
            }
        }

        return Encoding.UTF8;
    }

    private static bool IsRedirect(HttpStatusCode statusCode) => statusCode is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
