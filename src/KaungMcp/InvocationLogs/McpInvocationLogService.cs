using System.Diagnostics;
using KaungMcp.Capabilities;
using KaungMcp.Clients;
using KaungMcp.Data;
using KaungMcp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.InvocationLogs;

public sealed class McpInvocationLogService(
    IDbContextFactory<KaungMcpDbContext> dbContextFactory,
    ILogger<McpInvocationLogService> logger, CapabilityRegistry? capabilities = null)
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
        string source = InvocationSources.McpClient,
        string? correlationId = null, string? ownerId = null, string? ownerKind = null)
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
                CorrelationId = correlationId,
                OwnerId = ownerId ?? capabilities?.ResolveOwner(requestMethod, targetName),
                OwnerKind = ownerKind ?? (capabilities?.ResolveOwner(requestMethod, targetName) is { } owner ? owner == "host" ? "host" : capabilities.Kind(owner) : null),
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
        CancellationToken cancellationToken, string? ownerId = null, string? ownerKind = null, string? correlationId = null)
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

        if (!string.IsNullOrWhiteSpace(ownerId)) query = query.Where(item => item.OwnerId == ownerId);
        if (!string.IsNullOrWhiteSpace(ownerKind)) query = query.Where(item => item.OwnerKind == ownerKind);
        if (!string.IsNullOrWhiteSpace(correlationId)) query = query.Where(item => item.CorrelationId == correlationId);
        var totalCount = await query.CountAsync(cancellationToken);
        // Keep large request and response bodies out of paginated list queries.
        var entities = await query.OrderByDescending(item => item.StartedAtUtc).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(item => new InvocationLogResponse(item.Id, item.ClientApplicationId,
                item.ClientApplication != null ? item.ClientApplication.Name : item.Source == InvocationSources.Scheduler ? "定时任务" : "后台测试",
                item.Source, item.ToolName, item.RequestMethod, item.TargetName, item.StartedAtUtc, item.DurationMilliseconds, item.Succeeded,
                item.ErrorCode, item.ErrorMessage, item.TraceId, item.InputTokens, item.OutputTokens, item.TotalTokens, item.BillableUnits, item.CostMicros, item.CorrelationId)
                { OwnerId = item.OwnerId, OwnerKind = item.OwnerKind }).ToListAsync(cancellationToken);
        var names = await dbContext.PluginStates.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        var correlations = entities.Where(e => e.CorrelationId != null).Select(e => e.CorrelationId!).ToArray();
        var runs = await dbContext.TaskRuns.AsNoTracking().Where(r => correlations.Contains(r.CorrelationId)).ToListAsync(cancellationToken);
        var items = entities.Select(item => Enrich(item, names, runs.FirstOrDefault(r => r.CorrelationId == item.CorrelationId)?.Id)).ToArray();
        return new PagedResponse<InvocationLogResponse>(items, page, pageSize, totalCount);
    }

    public async Task<InvocationLogDetailResponse?> GetAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.InvocationLogs.AsNoTracking().Include(l => l.ClientApplication).SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (item is null) return null;
        var names = await db.PluginStates.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
        Guid? runId = item.CorrelationId is null ? null : await db.TaskRuns.Where(r => r.CorrelationId == item.CorrelationId).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(cancellationToken);
        return new(View(item, names, runId), item.RequestParametersJson, item.ResponseBody);
    }
    private InvocationLogResponse View(McpInvocationLog item, Dictionary<string, string> names, Guid? runId) => Enrich(new(
        item.Id, item.ClientApplicationId, item.ClientApplication?.Name ?? (item.Source == InvocationSources.Scheduler ? "定时任务" : "后台测试"), item.Source,
        item.ToolName, item.RequestMethod, item.TargetName, item.StartedAtUtc, item.DurationMilliseconds, item.Succeeded, item.ErrorCode, item.ErrorMessage,
        item.TraceId, item.InputTokens, item.OutputTokens, item.TotalTokens, item.BillableUnits, item.CostMicros, item.CorrelationId)
        { OwnerId = item.OwnerId, OwnerKind = item.OwnerKind }, names, runId);
    private InvocationLogResponse Enrich(InvocationLogResponse item, Dictionary<string, string> names, Guid? runId) => item with
    {
        OwnerName = item.OwnerId is null ? "归属未知" : item.OwnerId == "host" ? "系统" : names.GetValueOrDefault(item.OwnerId, capabilities?.Name(item.OwnerId) ?? item.OwnerId),
        TargetDisplayName = item.OwnerId is { } owner && item.TargetName is { } target ? capabilities?.TargetName(owner, target.Contains('/') && item.RequestMethod is "tasks/execute" or "tools/call" ? target.Split('/')[^1] : target) ?? target : item.TargetName,
        TaskRunId = runId
    };

    private static string? Truncate(string? value, int maximumLength) =>
        value is null || value.Length <= maximumLength ? value : value[..maximumLength];
}
