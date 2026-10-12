using System.Net;
using KaungMcp.Web;
using KaungMcp.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
namespace KaungMcp.Foundation;

public static class FoundationServices
{
    public static IServiceCollection AddMcpFoundation(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WebFetchOptions>()
            .Bind(configuration.GetSection(WebFetchOptions.SectionName))
            .Validate(options => options.TimeoutSeconds is >= 1 and <= 60, "WebFetch timeout must be between 1 and 60 seconds.")
            .Validate(options => options.MaxResponseBytes is >= 65_536 and <= 10_485_760, "WebFetch response limit is invalid.")
            .Validate(options => options.MaxRedirects is >= 0 and <= 10, "WebFetch redirect limit is invalid.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.UserAgent), "WebFetch user agent is required.")
            ;
        services.AddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
        services.AddSingleton<PublicWebAddressPolicy>();

        services.AddSingleton<ISafeHttpFetcher, SafeHttpFetcher>();
        services.AddSingleton<IRobotsPolicy, RobotsPolicy>();
        services.AddHttpClient("WebFetch")
            .ConfigureHttpClient((services, client) =>
            {
                var configured = services.GetRequiredService<ManagedSettingsStore>().FetchOptions;
                client.Timeout = TimeSpan.FromSeconds(configured.TimeoutSeconds);
            })
            .ConfigurePrimaryHttpMessageHandler(services =>
            {
                var addressPolicy = services.GetRequiredService<PublicWebAddressPolicy>();
                return new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    AutomaticDecompression = DecompressionMethods.All,
                    ConnectCallback = addressPolicy.ConnectAsync,
                    // A process-wide HTTP(S)_PROXY would make ConnectCallback validate the
                    // proxy endpoint rather than the requested origin, defeating DNS pinning.
                    UseProxy = false,
                    UseCookies = false,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
                };
            });

        return services;
    }
}
