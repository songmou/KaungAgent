using System.Text.Json;
using KaungAgent.Configuration;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace KaungAgent.Mcp;

public sealed class McpGatewayClient(
    IHttpClientFactory httpClientFactory,
    IOptions<McpGatewayOptions> options,
    ILoggerFactory loggerFactory) : IMcpGatewayClient
{
    private readonly McpGatewayOptions _options = options.Value;

    public async Task<IReadOnlyList<McpToolDescriptor>> ListToolsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        await using var client = await CreateClientAsync(timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        return tools
            .Select(tool => new McpToolDescriptor(
                tool.Name,
                tool.Title,
                tool.Description,
                tool.ProtocolTool.InputSchema,
                tool.ProtocolTool.OutputSchema))
            .ToList();
    }

    public async Task<InvokeToolResponse> InvokeToolAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        await using var client = await CreateClientAsync(timeout.Token);
        Dictionary<string, object?> normalizedArguments = arguments.ToDictionary(
            item => item.Key,
            item => item.Value);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        var tool = tools.SingleOrDefault(item => string.Equals(item.Name, toolName, StringComparison.Ordinal));
        if (tool is null)
        {
            throw new McpProtocolException($"MCP tool '{toolName}' was not found.", McpErrorCode.InvalidParams);
        }

        var result = await tool.CallAsync(normalizedArguments, cancellationToken: timeout.Token);

        var content = JsonSerializer.SerializeToElement(result.Content, McpJsonUtilities.DefaultOptions);
        JsonElement? structuredContent = result.StructuredContent is null
            ? null
            : JsonSerializer.SerializeToElement(result.StructuredContent, McpJsonUtilities.DefaultOptions);
        return new InvokeToolResponse(result.IsError == true, content, structuredContent);
    }

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        using var timeout = CreateTimeout(cancellationToken);
        await using var client = await CreateClientAsync(timeout.Token);
        await client.ListToolsAsync(cancellationToken: timeout.Token);
    }

    private async Task<McpClient> CreateClientAsync(CancellationToken cancellationToken)
    {
        var httpClient = httpClientFactory.CreateClient("KaungMcp");
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(_options.Endpoint),
                Name = "KaungMcp",
                TransportMode = HttpTransportMode.StreamableHttp,
                ConnectionTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
                AdditionalHeaders = new Dictionary<string, string>
                {
                    ["Authorization"] = $"Bearer {_options.ApiKey}"
                }
            },
            httpClient,
            loggerFactory,
            ownsHttpClient: false);

        try
        {
            return await McpClient.CreateAsync(transport, loggerFactory: loggerFactory, cancellationToken: cancellationToken);
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private CancellationTokenSource CreateTimeout(CancellationToken cancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        return source;
    }
}
