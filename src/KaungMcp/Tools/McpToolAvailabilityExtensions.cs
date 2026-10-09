using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;

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
            .AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var service = context.Services!.GetRequiredService<McpToolManagementService>();
                var name = context.Params?.Name ?? "";
                if (!await service.IsEnabledAsync(name, cancellationToken))
                    throw new McpException($"Tool '{name}' is disabled by the administrator.");
                return await next(context, cancellationToken);
            }));
}
