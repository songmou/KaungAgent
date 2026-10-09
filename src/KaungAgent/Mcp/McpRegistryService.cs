using System.Diagnostics;
using System.Text.Json;
using KaungAgent.Data;
using KaungAgent.Data.Entities;
using KaungAgent.Security;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace KaungAgent.Mcp;

public sealed class McpRegistryService(
    IDbContextFactory<KaungAgentDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    ProtectedSecretStore secrets)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<McpServerResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.McpServers.AsNoTracking().OrderBy(item => item.Name).ToListAsync(cancellationToken);
        return items.Select(ToResponse).ToList();
    }

    public async Task<McpServerResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.McpServers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return item is null ? null : ToResponse(item);
    }

    public async Task<McpServerResponse> SaveAsync(Guid? id, SaveMcpServerRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var headers = ParseHeaders(request.HeadersJson);
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = id is null
            ? new McpServerRegistration { Id = Guid.NewGuid(), Name = "", Endpoint = "", CreatedAtUtc = now }
            : await db.McpServers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("MCP 服务不存在。");
        if (entity.IsManaged && id is not null)
            throw new InvalidOperationException("内置 MCP 连接由启动配置管理，只能启用或禁用。");
        if (id is null) db.McpServers.Add(entity);
        entity.Name = request.Name.Trim();
        entity.Description = request.Description?.Trim();
        entity.Endpoint = request.Endpoint.Trim();
        entity.TimeoutSeconds = request.TimeoutSeconds;
        entity.IsEnabled = request.IsEnabled;
        entity.UpdatedAtUtc = now;
        if (request.HeadersJson is not null) entity.ProtectedHeadersJson = secrets.ProtectHeaders(headers);
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(entity);
    }

    public async Task SetEnabledAsync(Guid id, bool enabled, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.McpServers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("MCP 服务不存在。");
        entity.IsEnabled = enabled;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.McpServers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("MCP 服务不存在。");
        if (entity.IsManaged) throw new InvalidOperationException("内置 MCP 连接不能删除。");
        db.McpServers.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<McpCapabilitySnapshot> RefreshAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.McpServers.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("MCP 服务不存在。");
        var now = DateTimeOffset.UtcNow;
        try
        {
            await using var client = await CreateClientAsync(entity, cancellationToken);
            var capabilities = client.ServerCapabilities;
            IList<McpClientTool> tools = capabilities.Tools is null
                ? []
                : await client.ListToolsAsync(cancellationToken: cancellationToken);
            IList<McpClientPrompt> prompts = capabilities.Prompts is null
                ? []
                : await client.ListPromptsAsync(cancellationToken: cancellationToken);
            IList<McpClientResource> resources = capabilities.Resources is null
                ? []
                : await client.ListResourcesAsync(cancellationToken: cancellationToken);
            IList<McpClientResourceTemplate> templates = capabilities.Resources is null
                ? []
                : await client.ListResourceTemplatesAsync(cancellationToken: cancellationToken);

            var snapshot = new McpCapabilitySnapshot(
                JsonSerializer.SerializeToElement(client.ServerInfo, JsonOptions),
                JsonSerializer.SerializeToElement(client.ServerCapabilities, JsonOptions),
                client.ServerInstructions,
                tools.Select(tool => new McpCapabilityItem(
                    tool.Name, tool.Name, tool.Title, tool.Description, null, null,
                    JsonSerializer.SerializeToElement(tool.ProtocolTool, JsonOptions))).ToList(),
                prompts.Select(prompt => new McpCapabilityItem(
                    prompt.Name, prompt.Name, prompt.Title, prompt.Description, null, null,
                    JsonSerializer.SerializeToElement(prompt.ProtocolPrompt, JsonOptions))).ToList(),
                resources.Select(resource => new McpCapabilityItem(
                    resource.Uri, resource.Name, resource.Title, resource.Description, resource.Uri, resource.MimeType,
                    JsonSerializer.SerializeToElement(resource.ProtocolResource, JsonOptions))).ToList(),
                templates.Select(template => new McpCapabilityItem(
                    template.UriTemplate, template.Name, template.Title, template.Description, template.UriTemplate, template.MimeType,
                    JsonSerializer.SerializeToElement(template.ProtocolResourceTemplate, JsonOptions))).ToList(),
                now);

            entity.CapabilitySnapshotJson = JsonSerializer.Serialize(snapshot, JsonOptions);
            entity.LastStatus = "online";
            entity.LastError = null;
            entity.LastCheckedAtUtc = now;
            entity.LastSyncedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            return snapshot;
        }
        catch (Exception exception)
        {
            entity.LastStatus = "offline";
            entity.LastError = exception.Message.Length > 800 ? exception.Message[..800] : exception.Message;
            entity.LastCheckedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            throw;
        }
    }

    public async Task<McpOperationResult> InvokeToolAsync(
        Guid serverId, string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var entity = await GetEnabledEntityAsync(serverId, cancellationToken);
            await using var client = await CreateClientAsync(entity, cancellationToken);
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken);
            var tool = tools.SingleOrDefault(item => item.Name == toolName)
                ?? throw new KeyNotFoundException($"工具 {toolName} 不存在。");
            var normalizedArguments = McpArgumentNormalizer.Normalize(tool.ProtocolTool.InputSchema, arguments);
            var result = await tool.CallAsync(normalizedArguments, cancellationToken: cancellationToken);
            return new(result.IsError != true, JsonSerializer.SerializeToElement(result, JsonOptions), watch.ElapsedMilliseconds,
                result.IsError == true ? "MCP 工具返回错误。" : null);
        }
        catch (Exception exception)
        {
            return new(false, JsonSerializer.SerializeToElement(new { error = exception.Message }), watch.ElapsedMilliseconds, exception.Message);
        }
    }

    public async Task<McpOperationResult> GetPromptAsync(
        Guid serverId, string promptName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var entity = await GetEnabledEntityAsync(serverId, cancellationToken);
            await using var client = await CreateClientAsync(entity, cancellationToken);
            var result = await client.GetPromptAsync(promptName, arguments, cancellationToken: cancellationToken);
            return new(true, JsonSerializer.SerializeToElement(result, JsonOptions), watch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            return new(false, JsonSerializer.SerializeToElement(new { error = exception.Message }), watch.ElapsedMilliseconds, exception.Message);
        }
    }

    public async Task<McpOperationResult> ReadResourceAsync(
        Guid serverId, string uri, IReadOnlyDictionary<string, object?>? arguments, CancellationToken cancellationToken = default)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var entity = await GetEnabledEntityAsync(serverId, cancellationToken);
            await using var client = await CreateClientAsync(entity, cancellationToken);
            var result = arguments is { Count: > 0 }
                ? await client.ReadResourceAsync(uri, arguments, cancellationToken: cancellationToken)
                : await client.ReadResourceAsync(uri, cancellationToken: cancellationToken);
            return new(true, JsonSerializer.SerializeToElement(result, JsonOptions), watch.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            return new(false, JsonSerializer.SerializeToElement(new { error = exception.Message }), watch.ElapsedMilliseconds, exception.Message);
        }
    }

    public async Task SeedManagedLocalAsync(string endpoint, string apiKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey)) return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.McpServers.SingleOrDefaultAsync(item => item.ManagedKey == "kaung-local", cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (entity is null)
        {
            entity = new McpServerRegistration
            {
                Id = Guid.NewGuid(), Name = "KaungMcp（本地）", Description = "由一键启动脚本管理的本地 MCP 服务",
                Endpoint = endpoint, IsEnabled = true, IsManaged = true, ManagedKey = "kaung-local",
                TimeoutSeconds = 30, CreatedAtUtc = now
            };
            db.McpServers.Add(entity);
        }
        entity.Endpoint = endpoint;
        entity.ProtectedHeadersJson = secrets.ProtectHeaders(new Dictionary<string, string> { ["Authorization"] = $"Bearer {apiKey}" });
        entity.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<McpServerRegistration> GetEnabledEntityAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.McpServers.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id && item.IsEnabled, cancellationToken)
            ?? throw new KeyNotFoundException("MCP 服务不存在或已禁用。");
    }

    private async Task<McpClient> CreateClientAsync(McpServerRegistration entity, CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient("McpRegistry");
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(entity.Endpoint),
            Name = entity.Name,
            TransportMode = HttpTransportMode.StreamableHttp,
            EnableStandaloneGetStream = false,
            OwnsSession = false,
            ConnectionTimeout = TimeSpan.FromSeconds(entity.TimeoutSeconds),
            AdditionalHeaders = secrets.UnprotectHeaders(entity.ProtectedHeadersJson).ToDictionary(item => item.Key, item => item.Value)
        }, httpClient, loggerFactory, ownsHttpClient: false);
        try
        {
            return await McpClient.CreateAsync(
                transport,
                new McpClientOptions { ProtocolVersion = "2025-06-18" },
                loggerFactory,
                cancellationToken);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private McpServerResponse ToResponse(McpServerRegistration entity) => new(
        entity.Id, entity.Name, entity.Description, entity.Endpoint,
        secrets.UnprotectHeaders(entity.ProtectedHeadersJson).Keys.Order().ToList(),
        entity.TimeoutSeconds, entity.IsEnabled, entity.IsManaged, entity.LastStatus, entity.LastError,
        entity.LastCheckedAtUtc, entity.LastSyncedAtUtc,
        string.IsNullOrWhiteSpace(entity.CapabilitySnapshotJson)
            ? null : JsonSerializer.Deserialize<McpCapabilitySnapshot>(entity.CapabilitySnapshotJson, JsonOptions));

    private static Dictionary<string, string> ParseHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []; }
        catch (JsonException exception) { throw new ArgumentException("请求头必须是字符串键值 JSON 对象。", exception); }
    }

    private static void Validate(SaveMcpServerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("名称不能为空。");
        if (!Uri.TryCreate(request.Endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Endpoint 必须是有效的 HTTP/HTTPS 地址。");
        if (request.TimeoutSeconds is < 1 or > 300) throw new ArgumentException("超时必须在 1 到 300 秒之间。");
    }
}
