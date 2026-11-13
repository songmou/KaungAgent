using System.Net;
using KaungMcp.Configuration;
using KaungMcp.Plugin;

namespace KaungMcp.Web;

public sealed record BinaryDownloadInfo(string Name, string? ContentType, long Size);
public interface ISafeBinaryDownloader
{
    Task<BinaryDownloadInfo> DownloadAsync(Uri uri, Stream destination, long maxBytes, CancellationToken ct);
}

public sealed class SafeBinaryDownloader(IHttpClientFactory clients, PublicWebAddressPolicy addresses,
    ManagedSettingsStore settings) : ISafeBinaryDownloader
{
    public async Task<BinaryDownloadInfo> DownloadAsync(Uri uri, Stream destination, long maxBytes, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        var options = settings.FetchOptions;
        for (var redirect = 0; redirect <= options.MaxRedirects; redirect++)
        {
            await addresses.ValidateUriAsync(uri, token);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(options.UserAgent);
            using var response = await clients.CreateClient("BinaryDownload").SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or
                HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location ?? throw new ArgumentException("下载重定向缺少地址。");
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }
            if (!response.IsSuccessStatusCode) throw new ArgumentException($"文件下载失败：HTTP {(int)response.StatusCode}。");
            if (response.Content.Headers.ContentLength > maxBytes) throw new ContentLimitException("文件超过大小限制。");
            await using var source = await response.Content.ReadAsStreamAsync(token);
            var buffer = new byte[65536];
            long size = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, token)) != 0)
            {
                size += read;
                if (size > maxBytes) throw new ContentLimitException("文件超过大小限制。");
                await destination.WriteAsync(buffer.AsMemory(0, read), token);
            }
            var disposition = response.Content.Headers.ContentDisposition;
            var name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"') ?? Uri.UnescapeDataString(uri.AbsolutePath.Split('/').Last());
            return new(name, response.Content.Headers.ContentType?.MediaType, size);
        }
        throw new ArgumentException("文件下载超过重定向上限。");
    }
}
