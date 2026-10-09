using Microsoft.Extensions.DependencyInjection;

namespace KaungMcp.InvocationLogs;

public static class McpInvocationLoggingExtensions
{
    internal static readonly object ToolExceptionKey = new();

    public static IMcpServerBuilder AddInvocationLogging(this IMcpServerBuilder builder) =>
        builder.WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
        {
            try { return await next(context, cancellationToken); }
            catch (Exception exception)
            {
                // The SDK converts tool exceptions to isError results. Preserve the existing
                // error classification while the HTTP middleware records the actual response.
                var httpContext = context.Services?.GetService<IHttpContextAccessor>()?.HttpContext;
                if (httpContext is not null) httpContext.Items[ToolExceptionKey] = exception;
                throw;
            }
        }));
}
