using System.Net.Http.Headers;

namespace KaungAgent.Mcp;

public sealed class StrictJsonContentTypeHandler : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        MediaTypeHeaderValue? contentType = request.Content?.Headers.ContentType;
        if (contentType is not null &&
            string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            contentType.CharSet = null;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
