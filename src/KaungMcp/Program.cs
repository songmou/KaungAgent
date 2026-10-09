using KaungMcp.Api;
using KaungMcp.Clients;
using KaungMcp.Configuration;
using KaungMcp.Data;
using KaungMcp.InvocationLogs;
using KaungMcp.Security;
using KaungMcp.Web;
using KaungMcp.Jewelry;
using KaungMcp.Scheduling;
using KaungMcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using System.Net;

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!Directory.Exists(webRoot))
{
    var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (directory is not null)
    {
        var candidate = Path.Combine(directory.FullName, "src/KaungMcp/wwwroot");
        if (Directory.Exists(candidate)) { webRoot = candidate; break; }
        directory = directory.Parent;
    }
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, WebRootPath = webRoot });

var environmentAdminKey = Environment.GetEnvironmentVariable("KAUNG_MCP_ADMIN_KEY");
if (!string.IsNullOrWhiteSpace(environmentAdminKey))
{
    builder.Configuration[$"{AdminAuthOptions.SectionName}:Key"] = environmentAdminKey;
}

var searXngEndpoint = Environment.GetEnvironmentVariable("SEARXNG_ENDPOINT");
if (!string.IsNullOrWhiteSpace(searXngEndpoint))
{
    builder.Configuration[$"{WebSearchOptions.SectionName}:SearXng:BaseUrl"] = searXngEndpoint;
}

var searXngDisabled = Environment.GetEnvironmentVariable("SEARXNG_DISABLED");
if (searXngDisabled is "1" || bool.TryParse(searXngDisabled, out var disableSearXng) && disableSearXng)
{
    builder.Configuration[$"{WebSearchOptions.SectionName}:SearXng:Enabled"] = false.ToString();
}

var staticSearchEndpoint = Environment.GetEnvironmentVariable("STATIC_SEARCH_ENDPOINT");
if (!string.IsNullOrWhiteSpace(staticSearchEndpoint))
{
    builder.Configuration[$"{WebSearchOptions.SectionName}:StaticFallback:BaseUrl"] = staticSearchEndpoint;
}

builder.Configuration["SearXngLauncher:Mode"] = Environment.GetEnvironmentVariable("SEARXNG_MODE") ??
    (searXngDisabled is "1" or "true" ? "disabled" : string.IsNullOrWhiteSpace(searXngEndpoint) ? "local" : "external");
builder.Configuration["SearXngLauncher:Port"] = Environment.GetEnvironmentVariable("SEARXNG_PORT") ?? "8080";
builder.Configuration["SearXngLauncher:ExternalEndpoint"] = searXngEndpoint ?? "";

var baseSettings = ManagedSettingsStore.CaptureBaseline(builder.Configuration);
var savedSettings = ManagedSettingsStore.Read(ManagedSettingsStore.ResolvePath(builder.Configuration));
if (args.Contains("--sync-admin-bootstrap", StringComparer.Ordinal))
{
    var newKey = Environment.GetEnvironmentVariable("KAUNG_MCP_ADMIN_KEY")
        ?? throw new InvalidOperationException("KAUNG_MCP_ADMIN_KEY is required.");
    var store = new ManagedSettingsStore(builder.Configuration, baseSettings);
    await store.RotateAdminKeyAsync(newKey, CancellationToken.None);
    return;
}
var composedSettings = ManagedSettingsStore.Compose(baseSettings, savedSettings.Overrides);
if (args.Contains("--print-launch-settings", StringComparer.Ordinal))
{
    Console.WriteLine($"MCP_LOCAL_PORT={composedSettings["McpHost:LocalPort"]}");
    Console.WriteLine($"SEARXNG_MODE={composedSettings["SearXngLauncher:Mode"]}");
    Console.WriteLine($"SEARXNG_PORT={composedSettings["SearXngLauncher:Port"]}");
    Console.WriteLine($"SEARXNG_EXTERNAL_ENDPOINT={composedSettings["SearXngLauncher:ExternalEndpoint"]}");
    Console.WriteLine($"SEARXNG_PORT_MANAGED={savedSettings.Overrides.ContainsKey("SearXngLauncher:Port").ToString().ToLowerInvariant()}");
    return;
}
builder.Configuration.AddInMemoryCollection(composedSettings.Select(item =>
    new KeyValuePair<string, string?>(item.Key, item.Value)));
builder.Services.AddSingleton(services => new ManagedSettingsStore(
    services.GetRequiredService<IConfiguration>(), baseSettings));
if (!builder.Environment.IsEnvironment("Testing"))
{
    var localPort = builder.Configuration.GetValue("McpHost:LocalPort", 5091);
    var lanEnabled = builder.Configuration.GetValue("McpHost:LanEnabled", false);
    builder.WebHost.ConfigureKestrel(server =>
    {
        server.ListenLocalhost(localPort);
        if (lanEnabled)
        {
            var address = IPAddress.Parse(builder.Configuration["McpHost:LanAddress"]!);
            var port = builder.Configuration.GetValue("McpHost:LanPort", 5443);
            var certificate = Path.GetFullPath(builder.Configuration["McpManagement:CertificatePath"] ?? ManagedSettingsStore.DefaultCertificatePath);
            server.Listen(address, port, listener => listener.UseHttps(certificate));
        }
    });
}

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOptions<AdminAuthOptions>()
    .Bind(builder.Configuration.GetSection(AdminAuthOptions.SectionName))
    .Validate(options => options.Key.Length >= 32 || savedSettings.AdminKeyHash is { Length: 64 },
        "The administrator API key must contain at least 32 characters.")
    .ValidateOnStart();

builder.Services.AddDbContextFactory<KaungMcpDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var configuredConnectionString = configuration.GetConnectionString("KaungMcp")
        ?? "Data Source=data/kaungmcp.db;Cache=Shared;Default Timeout=30";
    options.UseSqlite(configuredConnectionString);
});
builder.Services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
builder.Services.AddScoped<McpClientApplicationService>();
builder.Services.AddScoped<McpInvocationLogService>();
builder.Services.AddScoped<McpToolManagementService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddOptions<JewelryOptions>().Bind(builder.Configuration.GetSection("Jewelry"))
    .Validate(o => o.MarketIntervalMinutes is >= 15 and <= 1440 && o.BrandIntervalMinutes is >= 60 and <= 1440,
        "Jewelry intervals must be 15..1440 minutes for markets and 60..1440 for brands.")
    .Validate(o => o.TimeoutSeconds is >= 1 and <= 60, "Jewelry timeout must be 1..60 seconds.")
    .Validate(o => o.Sources.Keys.All(id => JewelryCatalog.Sources.Any(s => s.Id == id)), "Unknown Jewelry source ID.")
    .ValidateOnStart();
builder.Services.AddSingleton<IJewelryAdapter, JewelryAdapter>();
builder.Services.AddSingleton<JewelryService>();
builder.Services.AddSingleton<SchedulerService>();
if (!builder.Environment.IsEnvironment("Testing"))
    builder.Services.AddHostedService<ScheduledTaskWorker>();
builder.Services.AddOptions<WebSearchOptions>()
    .Bind(builder.Configuration.GetSection(WebSearchOptions.SectionName))
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 60, "WebSearch timeout must be between 1 and 60 seconds.")
    .Validate(options => Uri.TryCreate(options.SearXng.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https", "The SearXNG base URL must be an absolute HTTP or HTTPS URL.")
    .Validate(options => options.StaticFallback.TimeoutSeconds is >= 1 and <= 60,
        "Static search timeout must be between 1 and 60 seconds.")
    .Validate(options => options.StaticFallback.MaxResponseBytes is >= 65_536 and <= 5_242_880,
        "Static search response limit is invalid.")
    .Validate(options => Uri.TryCreate(options.StaticFallback.BaseUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo),
        "The static search fallback URL must be an absolute HTTPS URL without user information.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.StaticFallback.UserAgent),
        "Static search fallback user agent is required.")
    .ValidateOnStart();
builder.Services.AddOptions<WebFetchOptions>()
    .Bind(builder.Configuration.GetSection(WebFetchOptions.SectionName))
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 60, "WebFetch timeout must be between 1 and 60 seconds.")
    .Validate(options => options.MaxResponseBytes is >= 65_536 and <= 10_485_760, "WebFetch response limit is invalid.")
    .Validate(options => options.MaxRedirects is >= 0 and <= 10, "WebFetch redirect limit is invalid.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.UserAgent), "WebFetch user agent is required.")
    .ValidateOnStart();
builder.Services.AddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
builder.Services.AddSingleton<PublicWebAddressPolicy>();
builder.Services.AddSingleton<WebContentExtractor>();
builder.Services.AddSingleton<ISafeHttpFetcher, SafeHttpFetcher>();
builder.Services.AddSingleton<IRobotsPolicy, RobotsPolicy>();
builder.Services.AddSingleton<IBrowserPageRenderer, PlaywrightPageRenderer>();
builder.Services.AddSingleton<IWebPageService, WebPageService>();
builder.Services.AddSingleton<SearXngSearchProvider>();
builder.Services.AddSingleton<BingHtmlSearchProvider>();
builder.Services.AddSingleton<FallbackWebSearchProvider>();
builder.Services.AddSingleton<IWebSearchProvider>(services =>
    services.GetRequiredService<FallbackWebSearchProvider>());
builder.Services.AddSingleton<IWebSearchHealthService>(services =>
    services.GetRequiredService<FallbackWebSearchProvider>());
builder.Services.AddHttpClient("SearXng")
    .ConfigureHttpClient((services, client) =>
    {
        var configured = services.GetRequiredService<ManagedSettingsStore>().SearchOptions;
        client.Timeout = TimeSpan.FromSeconds(configured.TimeoutSeconds);
    });
builder.Services.AddHttpClient("StaticSearch")
    .ConfigureHttpClient((services, client) =>
    {
        var configured = services.GetRequiredService<ManagedSettingsStore>().SearchOptions;
        client.Timeout = TimeSpan.FromSeconds(configured.StaticFallback.TimeoutSeconds);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(configured.StaticFallback.UserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml;q=0.9");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
        UseCookies = false
    });
builder.Services.AddHttpClient("WebFetch")
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

builder.Services
    .AddAuthentication()
    .AddScheme<AuthenticationSchemeOptions, McpApiKeyAuthenticationHandler>(
        SecurityConstants.McpClientScheme,
        _ => { })
    .AddScheme<AuthenticationSchemeOptions, AdminApiKeyAuthenticationHandler>(
        SecurityConstants.AdminScheme,
        _ => { });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(SecurityConstants.McpClientPolicy, policy =>
        policy.AddAuthenticationSchemes(SecurityConstants.McpClientScheme).RequireAuthenticatedUser());
    options.AddPolicy(SecurityConstants.AdminPolicy, policy =>
        policy.AddAuthenticationSchemes(SecurityConstants.AdminScheme).RequireAuthenticatedUser());
});

builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .AddAuthorizationFilters()
    .AddToolAvailability()
    .AddInvocationLogging()
    .WithToolsFromAssembly(typeof(Program).Assembly)
    .WithPromptsFromAssembly(typeof(Program).Assembly)
    .WithResourcesFromAssembly(typeof(Program).Assembly);

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin") || context.Request.Path.StartsWithSegments("/api/admin"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        if (context.Request.Path.StartsWithSegments("/admin"))
            context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; frame-ancestors 'none'";
    }
    await next(context);
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<McpInvocationLoggingMiddleware>();

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", async (
    IDbContextFactory<KaungMcpDbContext> dbContextFactory,
    CancellationToken cancellationToken) =>
{
    await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
    return await dbContext.Database.CanConnectAsync(cancellationToken)
        ? Results.Ok(new { status = "ready" })
        : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/health/web", async (
    IWebSearchHealthService searchHealthService,
    IBrowserPageRenderer browserRenderer,
    CancellationToken cancellationToken) =>
{
    var searchHealth = await searchHealthService.GetHealthAsync(cancellationToken);
    var chromiumAvailable = await browserRenderer.IsAvailableAsync(cancellationToken);
    var healthy = searchHealth.Ready && chromiumAvailable;
    return Results.Json(
        new
        {
            status = healthy ? "ready" : "degraded",
            components = new
            {
                webSearch = searchHealth.Ready ? "ready" : "unavailable",
                searxng = searchHealth.SearXng,
                staticSearchFallback = searchHealth.StaticFallback,
                activeSearchProvider = searchHealth.ActiveProvider,
                staticFetcher = "ready",
                chromium = chromiumAvailable ? "ready" : "unavailable"
            }
        },
        statusCode: healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
});

app.MapGet("/", () => Results.Content("""
<!doctype html>
<html lang="zh-CN">
<head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>KaungMcp 状态</title>
<style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#f1f3ef;color:#20312b;font-family:-apple-system,BlinkMacSystemFont,"PingFang SC",sans-serif}.card{width:min(640px,calc(100% - 40px));background:#fffefa;border:1px solid #daddd6;border-radius:22px;padding:38px;box-shadow:0 20px 60px #22382d12}.mark{width:54px;height:54px;border-radius:16px;background:#183d32;color:#fff;display:grid;place-items:center;font:30px Georgia}h1{font:38px Georgia,"Songti SC",serif;margin:24px 0 8px}p{color:#6e7772;line-height:1.7}.status{display:flex;align-items:center;gap:9px;background:#e1f0e6;color:#176346;padding:10px 13px;border-radius:10px;width:max-content}.dot{width:9px;height:9px;border-radius:50%;background:#38b878}.endpoint{margin:22px 0;padding:15px;background:#f2f3ef;border-radius:10px;font-family:monospace}.actions{display:flex;gap:10px;flex-wrap:wrap}a{padding:11px 16px;border-radius:10px;text-decoration:none;color:#174d3e;border:1px solid #cbd7cf;font-weight:650}a.primary{background:#175c48;color:#fff;border-color:#175c48}</style></head>
<body><main class="card"><div class="mark">K</div><h1>KaungMcp 正在运行</h1><p>管理客户端、密钥、调用日志与服务配置。</p><div class="status"><span class="dot"></span>服务已启动</div><div class="endpoint">MCP Endpoint · /mcp</div><div class="actions"><a class="primary" href="/admin/">打开管理页面</a><a href="/health/ready">查看就绪状态</a></div></main></body>
</html>
""", "text/html; charset=utf-8"));

app.MapAdminEndpoints();
app.MapMcp("/mcp").RequireAuthorization(SecurityConstants.McpClientPolicy);

var connectionString = app.Configuration.GetConnectionString("KaungMcp")
    ?? "Data Source=data/kaungmcp.db;Cache=Shared;Default Timeout=30";
await DatabaseInitializer.InitializeAsync(app.Services, connectionString);
if (args.Contains("--jewelry-probe", StringComparer.Ordinal))
{
    var jewelry = app.Services.GetRequiredService<JewelryService>();
    await jewelry.CollectDueAsync(null, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        sources = await jewelry.StatusAsync(CancellationToken.None),
        markets = await jewelry.GetQuotesAsync(false, null, "latest", null, "CN", CancellationToken.None),
        brands = await jewelry.GetQuotesAsync(true, null, "latest", null, "CN", CancellationToken.None)
    }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true }));
    await app.DisposeAsync();
    return;
}
await app.RunAsync();

public partial class Program;
