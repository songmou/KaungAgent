using System.Net;
using System.Net.Sockets;

namespace KaungMcp.Web;

public interface IHostAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) =>
        Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class PublicWebAddressPolicy(IHostAddressResolver resolver)
{
    public async Task<IReadOnlyList<IPAddress>> ValidateUriAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        if (!uri.IsAbsoluteUri ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new WebFetchException("Only absolute HTTP and HTTPS URLs are allowed.");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new WebFetchException("URLs containing user information are not allowed.");
        }

        if (string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
        {
            throw new WebFetchException("Local and private network hosts are not allowed.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await resolver.ResolveAsync(uri.IdnHost, cancellationToken);
        }
        catch (Exception exception) when (exception is SocketException or ArgumentException)
        {
            throw new WebFetchException($"Unable to resolve host '{uri.Host}'.", exception);
        }

        if (addresses.Length == 0)
        {
            throw new WebFetchException($"Host '{uri.Host}' did not resolve to an address.");
        }

        if (addresses.Any(address => !IsPublicAddress(address)))
        {
            throw new WebFetchException("The URL resolves to a local, private, reserved, or otherwise unsafe address.");
        }

        return addresses;
    }

    public async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var scheme = context.InitialRequestMessage.RequestUri?.Scheme ?? Uri.UriSchemeHttps;
        var uri = new UriBuilder(scheme, context.DnsEndPoint.Host, context.DnsEndPoint.Port).Uri;
        var addresses = await ValidateUriAsync(uri, cancellationToken);
        Exception? lastException = null;

        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                lastException = exception;
                socket.Dispose();
                if (exception is OperationCanceledException)
                {
                    throw;
                }
            }
        }

        throw new HttpRequestException($"Unable to connect to public host '{context.DnsEndPoint.Host}'.", lastException);
    }

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                192 when bytes[1] == 0 => false,
                192 when bytes[1] == 2 => false,
                198 when bytes[1] is 18 or 19 => false,
                198 when bytes[1] == 51 && bytes[2] == 100 => false,
                203 when bytes[1] == 0 && bytes[2] == 113 => false,
                >= 224 => false,
                _ => true
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            address.IsIPv6LinkLocal ||
            address.IsIPv6Multicast ||
            address.IsIPv6SiteLocal)
        {
            return false;
        }

        if ((bytes[0] & 0xFE) == 0xFC)
        {
            return false;
        }

        return !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8);
    }
}

public sealed class WebFetchException : Exception
{
    public WebFetchException(string message) : base(message) { }
    public WebFetchException(string message, Exception innerException) : base(message, innerException) { }
}
