using KaungMcp.Api;
using KaungMcp.Capabilities;
using KaungMcp.Clients;
using KaungMcp.Configuration;
using KaungMcp.Data;
using KaungMcp.InvocationLogs;
using KaungMcp.Security;
using KaungMcp.Web;
using KaungMcp.Plugins;
using KaungMcp.Plugin;
using KaungMcp.Foundation;
using KaungMcp.Scheduling;
using KaungMcp.Tools;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
if (!File.Exists(Path.Combine(webRoot, "admin", "index.html")))
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
var originalConfiguration = new ConfigurationBuilder().AddInMemoryCollection(builder.Configuration.AsEnumerable()).Build();
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
builder.Configuration.AddInMemoryCollection(composedSettings.Select(item =>
    new KeyValuePair<string, string?>(item.Key, item.Value)));
if (args.Contains("--print-launch-settings", StringComparer.Ordinal))
{
    Console.WriteLine($"WEB_SEARCH_ENABLED={CapabilityRegistry.LaunchToolEnabled(builder.Configuration, "kaung_web_search", builder.Configuration.GetValue("WebTools:SearchEnabled", true)).ToString().ToLowerInvariant()}");
    Console.WriteLine($"WEB_FETCH_ENABLED={CapabilityRegistry.LaunchToolEnabled(builder.Configuration, "kaung_web_fetch", builder.Configuration.GetValue("WebTools:FetchEnabled", true)).ToString().ToLowerInvariant()}");
    Console.WriteLine($"WEB_BROWSER_ENABLED={bool.Parse(composedSettings["WebFetch:BrowserFallbackEnabled"]).ToString().ToLowerInvariant()}");
    Console.WriteLine($"SEARXNG_SEARCH_ENABLED={bool.Parse(composedSettings["WebSearch:SearXng:Enabled"]).ToString().ToLowerInvariant()}");
    Console.WriteLine($"MCP_LOCAL_PORT={composedSettings["McpHost:LocalPort"]}");
    Console.WriteLine($"SEARXNG_MODE={composedSettings["SearXngLauncher:Mode"]}");
    Console.WriteLine($"SEARXNG_PORT={composedSettings["SearXngLauncher:Port"]}");
    Console.WriteLine($"SEARXNG_EXTERNAL_ENDPOINT={composedSettings["SearXngLauncher:ExternalEndpoint"]}");
    Console.WriteLine($"SEARXNG_PORT_MANAGED={savedSettings.Overrides.ContainsKey("SearXngLauncher:Port").ToString().ToLowerInvariant()}");
    return;
}
builder.Services.AddSingleton(services => new ManagedSettingsStore(
    services.GetRequiredService<IConfiguration>(), baseSettings, originalConfiguration));
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

// Only a same-host reverse proxy may supply the original HTTPS scheme/client address.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    options.ForwardLimit = 1;
});

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
        ?? "Data Source=data/host.db;Cache=Shared;Default Timeout=30";
    options.UseSqlite(configuredConnectionString);
});
builder.Services.AddScoped<IApiKeyValidator, ApiKeyValidator>();
builder.Services.AddScoped<McpClientApplicationService>();
builder.Services.AddScoped<McpInvocationLogService>();
builder.Services.AddScoped<McpToolManagementService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMcpFoundation(builder.Configuration);
builder.Services.AddWebModule(builder.Configuration);
var pluginRuntime = PluginRuntime.Discover(builder.Services, builder.Configuration);
builder.Services.AddSingleton(pluginRuntime);
builder.Services.AddSingleton<CapabilityRegistry>();
builder.Services.AddSingleton<SettingsDefinitionService>();
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddSingleton<IPluginTaskService>(services => services.GetRequiredService<SchedulerService>());
if (!builder.Environment.IsEnvironment("Testing")) builder.Services.AddHostedService<ScheduledTaskWorker>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(125));

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

var mcpBuilder = builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .AddAuthorizationFilters()
    .AddToolAvailability()
    .AddInvocationLogging();
pluginRuntime.RegisterMcp(mcpBuilder);
mcpBuilder.WithToolsFromAssembly(typeof(WebModule).Assembly);

var app = builder.Build();
var activePlugins = app.Services.GetRequiredService<PluginRuntime>();

app.UseForwardedHeaders();
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

app.MapGet("/health/plugins", () => Results.Json(new { status = activePlugins.Plugins.Any(p => p.IsEnabled && p.Status != "ready") ? "degraded" : "ready" }));

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
    ?? "Data Source=data/host.db;Cache=Shared;Default Timeout=30";
await DatabaseInitializer.InitializeAsync(app.Services, connectionString);
await activePlugins.InitializeAsync(app.Services, CancellationToken.None);
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>().CreateDbContextAsync();
    await using (db) await app.Services.GetRequiredService<CapabilityRegistry>().InitializeAsync(db, CancellationToken.None);
}
await app.Services.GetRequiredService<SchedulerService>().InitializeAsync(CancellationToken.None);
await app.RunAsync();

public partial class Program;
