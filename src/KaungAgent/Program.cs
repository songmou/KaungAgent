using KaungAgent.Api;
using KaungAgent.Chat;
using KaungAgent.Components;
using KaungAgent.Configuration;
using KaungAgent.Data;
using KaungAgent.Mcp;
using KaungAgent.Models;
using KaungAgent.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;

var builder = WebApplication.CreateBuilder(args);

CopyEnvironmentSecret("KAUNG_AGENT_API_KEY", $"{AgentAuthOptions.SectionName}:ApiKey");
CopyEnvironmentSecret("KAUNG_MCP_API_KEY", $"{McpGatewayOptions.SectionName}:ApiKey");
CopyEnvironmentSecret("KAUNG_MCP_ENDPOINT", $"{McpGatewayOptions.SectionName}:Endpoint");
CopyEnvironmentSecret("DEEPSEEK_API_KEY", "DeepSeek:ApiKey");

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<AgentExceptionHandler>();
builder.Services.AddDbContextFactory<KaungAgentDbContext>((services, options) =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    var configuredConnectionString = configuration.GetConnectionString("KaungAgent")
        ?? "Data Source=data/kaungagent.db;Cache=Shared;Default Timeout=30";
    options.UseSqlite(configuredConnectionString);
});

var dataProtection = builder.Services.AddDataProtection().SetApplicationName("KaungAgent");
if (!builder.Environment.IsEnvironment("Testing"))
{
    var keyPath = builder.Configuration["DataProtection:KeyPath"] ?? ".kaung/keys";
    var absoluteKeyPath = Path.GetFullPath(keyPath, Directory.GetCurrentDirectory());
    Directory.CreateDirectory(absoluteKeyPath);
    if (!OperatingSystem.IsWindows())
    {
        File.SetUnixFileMode(absoluteKeyPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(absoluteKeyPath));
}
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped<ProtectedSecretStore>();
builder.Services.AddScoped<ModelProfileServiceAccessor>();
builder.Services.AddScoped<ModelProfileService>();
builder.Services.AddScoped<OpenAiCompatibleClient>();
builder.Services.AddScoped<McpRegistryService>();
builder.Services.AddTransient<StrictJsonContentTypeHandler>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<ChatOrchestrator>();
builder.Services.AddScoped<ChatDraftService>();
builder.Services.AddOptions<AgentAuthOptions>()
    .Bind(builder.Configuration.GetSection(AgentAuthOptions.SectionName))
    .Validate(options => options.ApiKey.Length >= 32, "The KaungAgent API key must contain at least 32 characters.")
    .ValidateOnStart();
builder.Services.AddOptions<McpGatewayOptions>()
    .Bind(builder.Configuration.GetSection(McpGatewayOptions.SectionName))
    .Validate(
        options => Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) &&
                   endpoint.Scheme is "http" or "https",
        "The MCP endpoint must be an absolute HTTP or HTTPS URL.")
    .Validate(options => options.ApiKey.StartsWith("kmcp_", StringComparison.Ordinal), "A valid MCP API key is required.")
    .Validate(options => options.TimeoutSeconds is >= 1 and <= 300, "MCP timeout must be between 1 and 300 seconds.")
    .ValidateOnStart();

builder.Services.AddHttpClient("KaungMcp", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddHttpClient("McpRegistry", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .AddHttpMessageHandler<StrictJsonContentTypeHandler>();
builder.Services.AddHttpClient("OpenAiCompatible", client => client.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddScoped<IMcpGatewayClient, McpGatewayClient>();

builder.Services
    .AddAuthentication(AgentSecurityConstants.Scheme)
    .AddScheme<AuthenticationSchemeOptions, AgentApiKeyAuthenticationHandler>(
        AgentSecurityConstants.Scheme,
        _ => { });
builder.Services.AddAuthorization(options =>
    options.AddPolicy(AgentSecurityConstants.Policy, policy =>
        policy.AddAuthenticationSchemes(AgentSecurityConstants.Scheme).RequireAuthenticatedUser()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", async (IMcpGatewayClient client, CancellationToken cancellationToken) =>
{
    try
    {
        await client.PingAsync(cancellationToken);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception exception) when (exception is HttpRequestException or McpException or TimeoutException or OperationCanceledException)
    {
        return Results.Json(
            new { status = "not_ready" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapMcpGatewayEndpoints();
app.MapWorkspaceEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

var connectionString = app.Configuration.GetConnectionString("KaungAgent")
    ?? "Data Source=data/kaungagent.db;Cache=Shared;Default Timeout=30";
await AgentDatabaseInitializer.InitializeAsync(app.Services, connectionString);
await using (var scope = app.Services.CreateAsyncScope())
{
    var modelProfiles = scope.ServiceProvider.GetRequiredService<ModelProfileService>();
    await modelProfiles.EnsureDeepSeekAsync(app.Configuration["DeepSeek:ApiKey"]);

    var registry = scope.ServiceProvider.GetRequiredService<McpRegistryService>();
    var mcpOptions = app.Configuration.GetSection(McpGatewayOptions.SectionName).Get<McpGatewayOptions>();
    if (mcpOptions is not null)
    {
        await registry.SeedManagedLocalAsync(mcpOptions.Endpoint, mcpOptions.ApiKey);
    }
}
await app.RunAsync();

void CopyEnvironmentSecret(string environmentName, string configurationKey)
{
    var value = Environment.GetEnvironmentVariable(environmentName);
    if (!string.IsNullOrWhiteSpace(value))
    {
        builder.Configuration[configurationKey] = value;
    }
}

public partial class Program;
