using Microsoft.Extensions.Options;
using KaungMcp.Configuration;
using Microsoft.Playwright;

namespace KaungMcp.Web;

public sealed class PlaywrightPageRenderer(
    PublicWebAddressPolicy addressPolicy,
    IOptions<WebFetchOptions> options,
    ILogger<PlaywrightPageRenderer> logger,
    ManagedSettingsStore? settings = null) : IBrowserPageRenderer, IAsyncDisposable
{
    private readonly SemaphoreSlim _renderLock = new(1, 1);
    private WebFetchOptions _options => settings?.FetchOptions ?? options.Value;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public async Task<string?> RenderAsync(Uri uri, CancellationToken cancellationToken)
    {
        await _renderLock.WaitAsync(cancellationToken);
        try
        {
            var browser = await GetBrowserAsync();
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                AcceptDownloads = false,
                IgnoreHTTPSErrors = false,
                JavaScriptEnabled = true,
                ServiceWorkers = ServiceWorkerPolicy.Block,
                UserAgent = _options.UserAgent
            });
            var page = await context.NewPageAsync();
            page.Popup += (_, popup) => _ = popup.CloseAsync();
            page.SetDefaultNavigationTimeout(_options.TimeoutSeconds * 1_000);
            page.SetDefaultTimeout(_options.TimeoutSeconds * 1_000);
            await page.RouteAsync("**/*", async route =>
            {
                var request = route.Request;
                if (request.ResourceType is "image" or "media" or "font")
                {
                    await route.AbortAsync();
                    return;
                }

                if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var requestUri))
                {
                    await route.AbortAsync();
                    return;
                }

                try
                {
                    await addressPolicy.ValidateUriAsync(requestUri, cancellationToken);
                    await route.ContinueAsync();
                }
                catch (Exception exception) when (exception is WebFetchException or OperationCanceledException)
                {
                    await route.AbortAsync();
                }
            });

            var response = await page.GotoAsync(uri.AbsoluteUri, new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = _options.TimeoutSeconds * 1_000
            });
            if (response is null || response.Status is 401 or 403 || response.Status >= 400)
            {
                return null;
            }

            await page.WaitForTimeoutAsync(750);
            return await page.ContentAsync();
        }
        catch (PlaywrightException exception)
        {
            throw new WebFetchException(
                "Chromium rendering is unavailable or could not load the requested page. Install the Playwright Chromium browser and try again.",
                exception);
        }
        finally
        {
            _renderLock.Release();
        }
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _renderLock.WaitAsync(cancellationToken);
            try
            {
                _ = await GetBrowserAsync();
                return true;
            }
            finally
            {
                _renderLock.Release();
            }
        }
        catch (Exception exception) when (exception is PlaywrightException or WebFetchException)
        {
            logger.LogDebug(exception, "Chromium availability check failed.");
            return false;
        }
    }

    private async Task<IBrowser> GetBrowserAsync()
    {
        if (_browser is { IsConnected: true })
        {
            return _browser;
        }

        _playwright ??= await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        return _browser;
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser is not null)
        {
            await _browser.DisposeAsync();
        }
        _playwright?.Dispose();
        _renderLock.Dispose();
    }
}
