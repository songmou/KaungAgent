using System.Net;
using KaungMcp.Plugin;
using KaungMcp.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace KaungMcp.Tests;

public sealed class SafeBinaryDownloadTests
{
    private sealed class Resolver : IHostAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
            Task.FromResult(new[] { host == "public.example" ? IPAddress.Parse("93.184.216.34") : IPAddress.Loopback });
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
    private static McpWebApplicationFactory Factory(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(services =>
    {
        services.RemoveAll<IHostAddressResolver>(); services.AddSingleton<IHostAddressResolver, Resolver>();
        services.AddHttpClient("BinaryDownload").ConfigurePrimaryHttpMessageHandler(() => new Handler(respond));
    }, pluginIds: ["content"]);

    [Fact]
    public async Task Binary_download_preserves_bytes_without_decoding()
    {
        byte[] bytes = [0, 255, 128, 10, 13, 0];
        await using var factory = Factory(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        using var http = factory.CreateClient();
        var downloader = factory.Services.GetRequiredService<ISafeBinaryDownloader>();
        using var output = new MemoryStream();
        var info = await downloader.DownloadAsync(new("https://public.example/test.pdf"), output, 100, default);
        Assert.Equal(bytes, output.ToArray()); Assert.Equal(6, info.Size); Assert.Equal("test.pdf", info.Name);
    }
    [Fact]
    public async Task Redirects_to_private_addresses_are_rejected_before_second_request()
    {
        var calls = 0;
        await using var factory = Factory(_ =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.Redirect);
            response.Headers.Location = new("http://private.example/secret.txt");
            return response;
        });
        using var http = factory.CreateClient();
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<WebFetchException>(() => factory.Services.GetRequiredService<ISafeBinaryDownloader>()
            .DownloadAsync(new("https://public.example/test.txt"), output, 100, default));
        Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Chunked_responses_cannot_bypass_size_limit()
    {
        await using var factory = Factory(_ => new(HttpStatusCode.OK)
        {
            Content = new StreamContent(new MemoryStream(new byte[1025]))
        });
        using var http = factory.CreateClient(); using var output = new MemoryStream();
        await Assert.ThrowsAsync<ContentLimitException>(() => factory.Services.GetRequiredService<ISafeBinaryDownloader>()
            .DownloadAsync(new("https://public.example/large.pdf"), output, 1024, default));
    }
}
