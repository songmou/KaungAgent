using KaungMcp.Plugins;
using KaungMcp.Capabilities;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
namespace KaungMcp.Tools;

public static class McpToolAvailabilityExtensions
{
    public static IMcpServerBuilder AddToolAvailability(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters
            .AddListToolsFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                var disabled = await context.Services!.GetRequiredService<McpToolManagementService>().GetDisabledNamesAsync(ct);
                result.Tools = result.Tools.Where(t => !disabled.Contains(t.Name)).ToList(); return result;
            })
            .AddListPromptsFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                var runtime = context.Services!.GetRequiredService<PluginRuntime>();
                result.Prompts = result.Prompts.Where(p => runtime.PromptAvailable(p.Name)).ToList(); return result;
            })
            .AddListResourcesFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                var runtime = context.Services!.GetRequiredService<PluginRuntime>();
                result.Resources = result.Resources.Where(r => runtime.ResourceAvailable(r.Uri)).ToList(); return result;
            })
            .AddListResourceTemplatesFilter(next => async (context, ct) =>
            {
                var result = await next(context, ct);
                var runtime = context.Services!.GetRequiredService<PluginRuntime>();
                result.ResourceTemplates = result.ResourceTemplates.Where(r => runtime.ResourceAvailable(r.UriTemplate)).ToList(); return result;
            })
            .AddGetPromptFilter(next => async (context, ct) =>
            {
                if (!context.Services!.GetRequiredService<PluginRuntime>().PromptAvailable(context.Params?.Name ?? "")) throw new McpException("提示词插件不可用。");
                return await next(context, ct);
            })
            .AddReadResourceFilter(next => async (context, ct) =>
            {
                var resource = context.MatchedPrimitive as McpServerResource;
                var ownerUri = resource?.ProtocolResourceTemplate?.UriTemplate ?? resource?.ProtocolResource?.Uri ?? context.Params?.Uri ?? "";
                if (!context.Services!.GetRequiredService<PluginRuntime>().ResourceAvailable(ownerUri)) throw new McpException("资源插件不可用。");
                return await next(context, ct);
            })
            .AddCallToolFilter(next => async (context, ct) =>
            {
                var name = context.Params?.Name ?? "";
                if (!await context.Services!.GetRequiredService<McpToolManagementService>().IsEnabledAsync(name, ct)) throw new McpException("工具或所属能力不可用。");
                if (context.Services!.GetRequiredService<CapabilityRegistry>().ToolPolicy(name)?.SanitizeErrors != true) return await next(context, ct);
                try { var result = await next(context, ct); return result.IsError == true ? DataError() : result; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception) { return DataError(); }
            }));
    private static CallToolResult DataError() => new() { IsError = true, Content = [new TextContentBlock { Text = "暂时无法提供数据，请检查查询参数或稍后重试。" }] };
}
