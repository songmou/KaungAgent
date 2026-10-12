using System.Net;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Text.Json;
using KaungAgent.Api;
using KaungAgent.Chat;
using KaungAgent.Mcp;
using KaungAgent.Models;
using KaungAgent.Security;
using KaungMcp;
using KaungMcp.Clients;
using KaungMcp.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using KaungMcp.Plugins;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KaungAgent.Tests;

public sealed class KaungAgentIntegrationTests
{
    [Fact]
    public async Task Protected_gateway_discovers_and_invokes_real_mcp_tools()
    {
        await using var mcpFactory = new McpTestFactory();
        using var adminClient = mcpFactory.CreateClient();
        adminClient.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpTestFactory.AdminKey);
        var createResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/clients",
            new CreateClientRequest("agent-gateway", null));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedClientResponse>();
        Assert.NotNull(created);

        using var mcpHandler = mcpFactory.Server.CreateHandler();
        await using var agentFactory = new AgentWebApplicationFactory(mcpHandler, created.ApiKey);
        using var anonymousAgentClient = agentFactory.CreateClient();
        var home = await anonymousAgentClient.GetAsync("/");
        home.EnsureSuccessStatusCode();
        Assert.Contains("Kaung Agent", await home.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var mcpPage = await anonymousAgentClient.GetAsync("/mcp");
        mcpPage.EnsureSuccessStatusCode();
        var unauthorized = await anonymousAgentClient.GetAsync("/api/mcp/tools");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        using var agentClient = agentFactory.CreateClient();
        agentClient.DefaultRequestHeaders.Add(AgentSecurityConstants.Header, AgentWebApplicationFactory.AgentApiKey);
        var tools = await agentClient.GetFromJsonAsync<List<McpToolDescriptor>>("/api/mcp/tools");
        Assert.NotNull(tools);
        Assert.Contains(tools, tool => tool.Name == "kaung_echo");

        var servers = await agentClient.GetFromJsonAsync<List<McpServerResponse>>("/api/mcp/servers/");
        Assert.NotNull(servers);
        var managedServer = Assert.Single(servers, server => server.IsManaged);
        var refreshResponse = await agentClient.PostAsync($"/api/mcp/servers/{managedServer.Id}/refresh", null);
        refreshResponse.EnsureSuccessStatusCode();
        var snapshot = await refreshResponse.Content.ReadFromJsonAsync<McpCapabilitySnapshot>();
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot.Tools, tool => tool.Name == "kaung_echo");
        Assert.Contains(snapshot.Prompts, prompt => prompt.Name == "kaung_connection_diagnostic");
        Assert.Contains(snapshot.Resources, resource => resource.Uri == "kaung://server/status");

        var response = await agentClient.PostAsJsonAsync(
            "/api/mcp/tools/kaung_echo/invoke",
            new { arguments = new { message = "through KaungAgent" } });
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<InvokeToolResponse>();
        Assert.NotNull(result);
        Assert.False(result.IsError);
        Assert.Contains("through KaungAgent", result.StructuredContent?.GetRawText(), StringComparison.Ordinal);

        var invalidArguments = await agentClient.PostAsJsonAsync(
            "/api/mcp/tools/kaung_echo/invoke",
            new { arguments = JsonSerializer.SerializeToElement("not-an-object") });
        Assert.Equal(HttpStatusCode.BadRequest, invalidArguments.StatusCode);

        var unknownTool = await agentClient.PostAsJsonAsync(
            "/api/mcp/tools/missing-tool/invoke",
            new { arguments = new { } });
        Assert.Equal(HttpStatusCode.NotFound, unknownTool.StatusCode);

        var modelCreate = await agentClient.PostAsJsonAsync("/api/models", new SaveModelProfileRequest
        {
            Name = "test-model",
            BaseUrl = "http://localhost/v1",
            ModelId = "test-chat",
            ApiKey = "secret-value",
            HeadersJson = "{\"X-Test\":\"secret-header\"}",
            IsEnabled = true,
            IsDefault = true
        });
        modelCreate.EnsureSuccessStatusCode();
        var model = await modelCreate.Content.ReadFromJsonAsync<ModelProfileResponse>();
        Assert.NotNull(model);
        Assert.True(model.HasApiKey);
        Assert.Contains("X-Test", model.HeaderNames);
        var responseJson = await modelCreate.Content.ReadAsStringAsync();
        Assert.DoesNotContain("secret-value", responseJson, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-header", responseJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capability_refresh_skips_lists_not_advertised_by_a_tools_only_server()
    {
        await using var mcpFactory = new McpTestFactory(toolsOnly: true);
        using var adminClient = mcpFactory.CreateClient();
        adminClient.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpTestFactory.AdminKey);
        var createResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/clients",
            new CreateClientRequest("tools-only-agent", null));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedClientResponse>();
        Assert.NotNull(created);

        using var recordingHandler = new RecordingHandler(mcpFactory.Server.CreateHandler());
        await using var agentFactory = new AgentWebApplicationFactory(recordingHandler, created.ApiKey);
        using var agentClient = agentFactory.CreateClient();
        agentClient.DefaultRequestHeaders.Add(AgentSecurityConstants.Header, AgentWebApplicationFactory.AgentApiKey);

        var servers = await agentClient.GetFromJsonAsync<List<McpServerResponse>>("/api/mcp/servers/");
        Assert.NotNull(servers);
        var managedServer = Assert.Single(servers, server => server.IsManaged);
        var refreshResponse = await agentClient.PostAsync($"/api/mcp/servers/{managedServer.Id}/refresh", null);
        refreshResponse.EnsureSuccessStatusCode();
        var snapshot = await refreshResponse.Content.ReadFromJsonAsync<McpCapabilitySnapshot>();
        Assert.NotNull(snapshot);
        Assert.Contains(snapshot.Tools, tool => tool.Name == "kaung_ping");
        Assert.Empty(snapshot.Prompts);
        Assert.Empty(snapshot.Resources);
        Assert.Empty(snapshot.ResourceTemplates);

        var refreshedServer = await agentClient.GetFromJsonAsync<McpServerResponse>(
            $"/api/mcp/servers/{managedServer.Id}");
        Assert.NotNull(refreshedServer);
        Assert.Equal("online", refreshedServer.LastStatus);
        Assert.Null(refreshedServer.LastError);
        Assert.DoesNotContain(recordingHandler.RequestBodies, body => body.Contains("\"method\":\"prompts/list\"", StringComparison.Ordinal));
        Assert.DoesNotContain(recordingHandler.RequestBodies, body => body.Contains("\"method\":\"resources/list\"", StringComparison.Ordinal));
        Assert.DoesNotContain(recordingHandler.RequestBodies, body => body.Contains("\"method\":\"resources/templates/list\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeepSeek_environment_configuration_is_optional_idempotent_and_preserves_an_existing_default()
    {
        await using var mcpFactory = new McpTestFactory();
        using var adminClient = mcpFactory.CreateClient();
        adminClient.DefaultRequestHeaders.Add(SecurityConstants.AdminHeader, McpTestFactory.AdminKey);
        var createResponse = await adminClient.PostAsJsonAsync(
            "/api/admin/clients",
            new CreateClientRequest("deepseek-bootstrap-agent", null));
        createResponse.EnsureSuccessStatusCode();
        var created = await createResponse.Content.ReadFromJsonAsync<CreatedClientResponse>();
        Assert.NotNull(created);

        await using (var unconfiguredFactory = new AgentWebApplicationFactory(
            mcpFactory.Server.CreateHandler(),
            created.ApiKey))
        {
            using var unconfiguredClient = unconfiguredFactory.CreateClient();
            unconfiguredClient.DefaultRequestHeaders.Add(
                AgentSecurityConstants.Header,
                AgentWebApplicationFactory.AgentApiKey);
            var unconfiguredModels = await unconfiguredClient.GetFromJsonAsync<List<ModelProfileResponse>>("/api/models/");
            Assert.NotNull(unconfiguredModels);
            Assert.Empty(unconfiguredModels);
        }

        const string initialApiKey = "test-deepseek-api-key-that-is-at-least-32-characters";
        const string rotatedApiKey = "rotated-deepseek-key-that-is-at-least-32-characters";
        await using var agentFactory = new AgentWebApplicationFactory(
            mcpFactory.Server.CreateHandler(),
            created.ApiKey,
            initialApiKey);
        using var agentClient = agentFactory.CreateClient();
        agentClient.DefaultRequestHeaders.Add(AgentSecurityConstants.Header, AgentWebApplicationFactory.AgentApiKey);

        var models = await agentClient.GetFromJsonAsync<List<ModelProfileResponse>>("/api/models/");
        var deepSeek = Assert.Single(models!);
        Assert.Equal(ModelProfileService.DeepSeekProfileName, deepSeek.Name);
        Assert.Equal(ModelProfileService.DeepSeekBaseUrl, deepSeek.BaseUrl);
        Assert.Equal(ModelProfileService.DeepSeekModelId, deepSeek.ModelId);
        Assert.True(deepSeek.HasApiKey);
        Assert.True(deepSeek.IsEnabled);
        Assert.True(deepSeek.IsDefault);

        var conversationResponse = await agentClient.PostAsJsonAsync(
            "/api/conversations/",
            new CreateConversationRequest(null, null));
        conversationResponse.EnsureSuccessStatusCode();

        await using var scope = agentFactory.Services.CreateAsyncScope();
        var modelService = scope.ServiceProvider.GetRequiredService<ModelProfileService>();
        var initialEntity = await modelService.GetEntityAsync(deepSeek.Id);
        Assert.NotNull(initialEntity);
        Assert.NotEqual(initialApiKey, initialEntity.ProtectedApiKey);
        Assert.Equal(initialApiKey, modelService.RevealSecrets(initialEntity).ApiKey);

        var repeated = await modelService.EnsureDeepSeekAsync(rotatedApiKey);
        Assert.NotNull(repeated);
        Assert.Equal(deepSeek.Id, repeated.Id);
        Assert.Single(await modelService.ListAsync());
        var rotatedEntity = await modelService.GetEntityAsync(deepSeek.Id);
        Assert.NotNull(rotatedEntity);
        Assert.Equal(rotatedApiKey, modelService.RevealSecrets(rotatedEntity).ApiKey);

        var manualDefault = await modelService.SaveAsync(null, new SaveModelProfileRequest
        {
            Name = "Manual Default",
            BaseUrl = "https://example.com/v1",
            ModelId = "manual-model",
            ApiKey = "manual-model-key",
            IsEnabled = true,
            IsDefault = true
        });
        await modelService.EnsureDeepSeekAsync(rotatedApiKey);

        var finalModels = await modelService.ListAsync();
        Assert.Equal(2, finalModels.Count);
        Assert.True(finalModels.Single(item => item.Id == manualDefault.Id).IsDefault);
        Assert.False(finalModels.Single(item => item.Id == deepSeek.Id).IsDefault);
    }

    private sealed class McpTestFactory(bool toolsOnly = false) : WebApplicationFactory<KaungMcpApplicationMarker>
    {
        public const string AdminKey = "agent-test-admin-key-that-is-at-least-32-characters";
        private readonly string _databasePath = Path.Combine(
            Path.GetTempPath(),
            $"kaungagent-mcp-tests-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            IConfiguration? testConfiguration = null;
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AdminAuth:Key"] = AdminKey,
                    ["AllowedHosts"] = "*",
                    ["Plugins:Directory"] = Path.Combine(AppContext.BaseDirectory, "plugins"),
                    ["Plugins:DataDirectory"] = _databasePath + ".plugins",
                    ["Logging:LogLevel:Default"] = "Warning",
                    ["ConnectionStrings:KaungMcp"] = $"Data Source={_databasePath};Default Timeout=30"
                });
                testConfiguration = configuration.Build();
            });
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<PluginRuntime>();
                services.RemoveAll<McpServerTool>();
                services.RemoveAll<McpServerPrompt>();
                services.RemoveAll<McpServerResource>();
                var runtime = PluginRuntime.Discover(services, testConfiguration!);
                services.AddSingleton(runtime);
                runtime.RegisterMcp(services.AddMcpServer());
            });
            if (toolsOnly)
            {
                builder.ConfigureServices(services => services.PostConfigure<McpServerOptions>(options =>
                {
                    options.PromptCollection = null!;
                    options.ResourceCollection = null!;
                    var capabilities = options.Capabilities ??= new ServerCapabilities();
                    capabilities.Prompts = null;
                    capabilities.Resources = null;
                }));
            }
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing)
            {
                return;
            }

            if (Directory.Exists(_databasePath + ".plugins")) Directory.Delete(_databasePath + ".plugins", true);
            foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }
    }

    private sealed class RecordingHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        public ConcurrentQueue<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                RequestBodies.Enqueue(await request.Content.ReadAsStringAsync(cancellationToken));
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }
}
