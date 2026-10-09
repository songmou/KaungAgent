using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using KaungMcp.Data;
using KaungMcp.Data.Entities;
using KaungMcp.InvocationLogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace KaungMcp.Tools;

public sealed class McpToolManagementService(
    IEnumerable<McpServerTool> registeredTools,
    IDbContextFactory<KaungMcpDbContext> dbContextFactory,
    McpInvocationLogService invocationLogs,
    IServiceProvider services,
    IOptions<McpServerOptions> serverOptions,
    ILoggerFactory loggerFactory)
{
    private readonly IReadOnlyDictionary<string, McpServerTool> tools = registeredTools
        .ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);

    public async Task<McpToolCatalogResponse> GetCatalogAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var settings = await dbContext.ToolSettings.AsNoTracking()
            .ToDictionaryAsync(item => item.ToolName, item => item.IsEnabled, StringComparer.Ordinal, cancellationToken);

        var groups = tools.Values
            .Select(tool => CreateCatalogItem(tool, settings.GetValueOrDefault(tool.ProtocolTool.Name, true)))
            .GroupBy(item => item.Category.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First().Category;
                return new McpToolCategoryResponse(
                    first.Id,
                    first.DisplayName,
                    first.Description,
                    first.Order,
                    group.Select(item => item.Tool)
                        .OrderBy(item => item.Title ?? item.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            })
            .OrderBy(category => category.Order)
            .ThenBy(category => category.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var allTools = groups.SelectMany(category => category.Tools).ToArray();
        return new McpToolCatalogResponse(
            groups,
            allTools.Length,
            allTools.Count(tool => tool.IsEnabled),
            allTools.Count(tool => !tool.IsEnabled));
    }

    public async Task<bool?> SetEnabledAsync(string name, bool isEnabled, CancellationToken cancellationToken)
    {
        if (!tools.ContainsKey(name)) return null;

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var setting = await dbContext.ToolSettings.FindAsync([name], cancellationToken);
        if (setting is null)
        {
            dbContext.ToolSettings.Add(new McpToolSetting
            {
                ToolName = name,
                IsEnabled = isEnabled,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
        }
        else
        {
            setting.IsEnabled = isEnabled;
            setting.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return isEnabled;
    }

    public async Task<bool> IsEnabledAsync(string name, CancellationToken cancellationToken)
    {
        if (!tools.ContainsKey(name)) return false;
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ToolSettings.AsNoTracking()
            .Where(item => item.ToolName == name)
            .Select(item => (bool?)item.IsEnabled)
            .SingleOrDefaultAsync(cancellationToken) ?? true;
    }

    public async Task<HashSet<string>> GetDisabledNamesAsync(CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.ToolSettings.AsNoTracking()
            .Where(item => !item.IsEnabled)
            .Select(item => item.ToolName)
            .ToHashSetAsync(StringComparer.Ordinal, cancellationToken);
    }

    public async Task<TestMcpToolResponse?> TestAsync(
        string name,
        JsonElement arguments,
        bool confirmRisk,
        CancellationToken cancellationToken,
        string source = InvocationSources.AdminTest)
    {
        if (!tools.TryGetValue(name, out var tool)) return null;
        if (source == InvocationSources.Scheduler && !await IsEnabledAsync(name, cancellationToken))
            throw new InvalidOperationException($"Tool '{name}' is disabled.");
        if (arguments.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Arguments must be a JSON object.", nameof(arguments));

        var annotations = tool.ProtocolTool.Annotations;
        var requiresConfirmation = annotations?.ReadOnlyHint == false || annotations?.DestructiveHint == true;
        if (requiresConfirmation && !confirmRisk)
            throw new RiskConfirmationRequiredException(name);

        var startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        CallToolResult result;
        string? error = null;
        try
        {
            var values = arguments.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
            await using var server = McpServer.Create(
                new AdminTestTransport(), serverOptions.Value, loggerFactory, services);
            var request = new JsonRpcRequest
            {
                Id = new RequestId(1),
                Method = RequestMethods.ToolsCall
            };
            var context = new RequestContext<CallToolRequestParams>(server, request, new CallToolRequestParams
            {
                Name = name,
                Arguments = values
            })
            {
                Services = services,
                MatchedPrimitive = tool
            };
            result = await tool.InvokeAsync(context, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            error = exception is McpException ? exception.Message : GetInnermostMessage(exception);
            result = ErrorResult(error);
        }
        timer.Stop();

        var succeeded = result.IsError != true;
        var requestPayload = JsonSerializer.Serialize(new { name, arguments });
        var responsePayload = JsonSerializer.Serialize(result, McpJsonUtilities.DefaultOptions);
        var logId = await invocationLogs.WriteAsync(
            null,
            "tools/call",
            name,
            startedAt,
            timer.ElapsedMilliseconds,
            succeeded,
            succeeded ? null : "tool_error",
            error ?? (succeeded ? null : "The tool returned an error result."),
            requestPayload,
            responsePayload,
            source);

        return new TestMcpToolResponse(
            succeeded,
            timer.ElapsedMilliseconds,
            result,
            error,
            logId,
            await IsEnabledAsync(name, cancellationToken));
    }

    private static (McpToolCategoryAttribute Category, McpToolResponse Tool) CreateCatalogItem(
        McpServerTool tool,
        bool isEnabled)
    {
        var category = tool.Metadata.OfType<McpToolCategoryAttribute>().FirstOrDefault()
            ?? new McpToolCategoryAttribute("other", "其他工具", int.MaxValue);
        var protocolTool = tool.ProtocolTool;
        var annotations = protocolTool.Annotations;
        var parameterCount = protocolTool.InputSchema.TryGetProperty("properties", out var properties) &&
            properties.ValueKind == JsonValueKind.Object ? properties.EnumerateObject().Count() : 0;
        var risky = annotations?.ReadOnlyHint == false || annotations?.DestructiveHint == true;

        return (category, new McpToolResponse(
            protocolTool.Name,
            protocolTool.Title ?? annotations?.Title,
            protocolTool.Description,
            protocolTool.InputSchema.Clone(),
            protocolTool.OutputSchema?.Clone(),
            annotations?.ReadOnlyHint,
            annotations?.DestructiveHint,
            annotations?.IdempotentHint,
            annotations?.OpenWorldHint,
            isEnabled,
            parameterCount,
            risky));
    }

    private static CallToolResult ErrorResult(string message) => new()
    {
        Content = [new TextContentBlock { Text = message }],
        IsError = true
    };

    private static string GetInnermostMessage(Exception exception)
    {
        while (exception.InnerException is not null) exception = exception.InnerException;
        return exception.Message;
    }

    private sealed class AdminTestTransport : ITransport
    {
        private readonly Channel<JsonRpcMessage> messages = Channel.CreateUnbounded<JsonRpcMessage>();

        public string? SessionId => null;

        public ChannelReader<JsonRpcMessage> MessageReader => messages.Reader;

        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            messages.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class RiskConfirmationRequiredException(string toolName)
    : InvalidOperationException($"Tool '{toolName}' requires explicit risk confirmation.");
