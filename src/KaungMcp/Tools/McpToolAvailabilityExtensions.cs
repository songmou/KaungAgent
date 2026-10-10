using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace KaungMcp.Tools;

public static class McpToolAvailabilityExtensions
{
    public static IMcpServerBuilder AddToolAvailability(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters
            .AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken);
                var service = context.Services!.GetRequiredService<McpToolManagementService>();
                var disabled = await service.GetDisabledNamesAsync(cancellationToken);
                result.Tools = result.Tools.Where(tool => !disabled.Contains(tool.Name)).ToList();
                return result;
            })
            .AddListPromptsFilter(next => async (context, ct) =>
                context.Services!.GetRequiredService<McpToolManagementService>().JewelryOnly
                    ? new ListPromptsResult { Prompts = [] } : await next(context, ct))
            .AddListResourcesFilter(next => async (context, ct) =>
                context.Services!.GetRequiredService<McpToolManagementService>().JewelryOnly
                    ? new ListResourcesResult { Resources = [] } : await next(context, ct))
            .AddListResourceTemplatesFilter(next => async (context, ct) =>
                context.Services!.GetRequiredService<McpToolManagementService>().JewelryOnly
                    ? new ListResourceTemplatesResult { ResourceTemplates = [] } : await next(context, ct))
            .AddGetPromptFilter(next => async (context, ct) =>
                context.Services!.GetRequiredService<McpToolManagementService>().JewelryOnly
                    ? throw new McpException("此服务仅提供行情数据工具。") : await next(context, ct))
            .AddReadResourceFilter(next => async (context, ct) =>
                context.Services!.GetRequiredService<McpToolManagementService>().JewelryOnly
                    ? throw new McpException("此服务仅提供行情数据工具。") : await next(context, ct))
            .AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var service = context.Services!.GetRequiredService<McpToolManagementService>();
                var name = context.Params?.Name ?? "";
                if (!await service.IsEnabledAsync(name, cancellationToken))
                    throw new McpException($"Tool '{name}' is disabled by the administrator.");
                if (!name.StartsWith("kaung_jewelry_", StringComparison.Ordinal))
                    return await next(context, cancellationToken);
                try
                {
                    var result = await next(context, cancellationToken);
                    return result.IsError == true ? DataError() : result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    return DataError();
                }
            }));
    private static CallToolResult DataError() => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = "暂时无法提供数据，请检查查询参数或稍后重试。" }]
    };
}
