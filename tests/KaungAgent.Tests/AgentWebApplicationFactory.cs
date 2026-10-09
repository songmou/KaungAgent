using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KaungAgent.Tests;

public sealed class AgentWebApplicationFactory(
    HttpMessageHandler mcpHandler,
    string mcpApiKey,
    string? deepSeekApiKey = null,
    HttpMessageHandler? modelHandler = null) : WebApplicationFactory<KaungAgentApplicationMarker>
{
    public const string AgentApiKey = "test-agent-key-that-is-at-least-32-characters";
    public string DatabasePath { get; } = Path.Combine(Path.GetTempPath(), $"kaungagent-tests-{Guid.NewGuid():N}.db");
    public string KeyPath { get; } = Path.Combine(Path.GetTempPath(), $"kaungagent-keys-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AgentAuth:ApiKey"] = AgentApiKey,
                ["Mcp:Endpoint"] = "http://localhost/mcp",
                ["Mcp:ApiKey"] = mcpApiKey,
                ["Mcp:TimeoutSeconds"] = "10",
                ["DeepSeek:ApiKey"] = deepSeekApiKey,
                ["ConnectionStrings:KaungAgent"] = $"Data Source={DatabasePath};Default Timeout=30",
                ["DataProtection:KeyPath"] = KeyPath,
                ["AllowedHosts"] = "*"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient("KaungMcp")
                .ConfigurePrimaryHttpMessageHandler(() => mcpHandler);
            services.AddHttpClient("McpRegistry")
                .ConfigurePrimaryHttpMessageHandler(() => mcpHandler);
            if (modelHandler is not null)
            {
                services.AddHttpClient("OpenAiCompatible")
                    .ConfigurePrimaryHttpMessageHandler(() => modelHandler);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        foreach (var path in new[] { DatabasePath, $"{DatabasePath}-wal", $"{DatabasePath}-shm" })
            if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(KeyPath)) Directory.Delete(KeyPath, true);
    }
}
