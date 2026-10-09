using System.Diagnostics;
using KaungMcp.Clients;
using KaungMcp.Data;
using KaungMcp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.InvocationLogs;

public sealed class McpInvocationLogService(
    IDbContextFactory<KaungMcpDbContext> dbContextFactory,
    ILogger<McpInvocationLogService> logger)
{
    public async Task<long?> WriteAsync(
        Guid? clientId,
        string requestMethod,
        string? targetName,
        DateTimeOffset startedAtUtc,
        long durationMilliseconds,
        bool succeeded,
        string? errorCode,
        string? errorMessage,
        string? requestParametersJson = null,
        string? responseBody = null,
        string source = InvocationSources.McpClient)
    {
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(CancellationToken.None);
            var entry = new McpInvocationLog
            {
                ClientApplicationId = clientId,
                Source = source,
                ToolName = requestMethod == "tools/call" ? targetName : null,
                RequestMethod = Truncate(requestMethod, 100) ?? "unknown",
                TargetName = Truncate(targetName, 200),
                StartedAtUtc = startedAtUtc,
                DurationMilliseconds = durationMilliseconds,
                Succeeded = succeeded,
                ErrorCode = Truncate(errorCode, 100),
                ErrorMessage = Truncate(errorMessage, 512),
                TraceId = Activity.Current?.TraceId.ToString(),
                RequestParametersJson = requestParametersJson,
                ResponseBody = responseBody
            };
            dbContext.InvocationLogs.Add(entry);
            await dbContext.SaveChangesAsync(CancellationToken.None);
            return entry.Id;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Unable to persist the invocation log for MCP client {ClientId} and request {Method}.",
                clientId,
                requestMethod);
            return null;
        }
    }

    public async Task<PagedResponse<InvocationLogResponse>> ListAsync(
        Guid? clientId,
        string? toolName,
        string? method,
        string? source,
        bool? succeeded,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.InvocationLogs.AsNoTracking().AsQueryable();
        if (clientId.HasValue)
        {
            query = query.Where(item => item.ClientApplicationId == clientId.Value);
        }

        if (!string.IsNullOrWhiteSpace(toolName))
        {
            var normalizedToolName = toolName.Trim();
            query = query.Where(item => item.ToolName == normalizedToolName);
        }

        if (!string.IsNullOrWhiteSpace(method))
        {
            var normalizedMethod = method.Trim();
            query = query.Where(item => item.RequestMethod == normalizedMethod);
        }

        if (!string.IsNullOrWhiteSpace(source))
        {
            var normalizedSource = source.Trim();
            if (!InvocationSources.IsValid(normalizedSource))
                throw new ArgumentException("Unknown invocation source.", nameof(source));
            query = query.Where(item => item.Source == normalizedSource);
        }

        if (succeeded.HasValue)
        {
            query = query.Where(item => item.Succeeded == succeeded.Value);
        }

        if (fromUtc.HasValue)
        {
            query = query.Where(item => item.StartedAtUtc >= fromUtc.Value);
        }

        if (toUtc.HasValue)
        {
            query = query.Where(item => item.StartedAtUtc <= toUtc.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(item => item.StartedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new InvocationLogResponse(
                item.Id,
                item.ClientApplicationId,
                    item.ClientApplication != null ? item.ClientApplication.Name : item.Source == InvocationSources.Scheduler ? "定时任务" : "后台测试",
                item.Source,
                item.ToolName,
                item.RequestMethod,
                item.TargetName,
                item.StartedAtUtc,
                item.DurationMilliseconds,
                item.Succeeded,
                item.ErrorCode,
                item.ErrorMessage,
                item.TraceId,
                item.InputTokens,
                item.OutputTokens,
                item.TotalTokens,
                item.BillableUnits,
                item.CostMicros))
            .ToListAsync(cancellationToken);

        return new PagedResponse<InvocationLogResponse>(items, page, pageSize, totalCount);
    }

    public async Task<InvocationLogDetailResponse?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.InvocationLogs.AsNoTracking()
            .Where(item => item.Id == id)
            .Select(item => new InvocationLogDetailResponse(
                new InvocationLogResponse(item.Id, item.ClientApplicationId,
                    item.ClientApplication != null ? item.ClientApplication.Name : item.Source == InvocationSources.Scheduler ? "定时任务" : "后台测试", item.Source,
                    item.ToolName, item.RequestMethod, item.TargetName, item.StartedAtUtc,
                    item.DurationMilliseconds, item.Succeeded, item.ErrorCode, item.ErrorMessage,
                    item.TraceId, item.InputTokens, item.OutputTokens, item.TotalTokens,
                    item.BillableUnits, item.CostMicros),
                item.RequestParametersJson, item.ResponseBody))
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength ? value : value[..maximumLength];
}
