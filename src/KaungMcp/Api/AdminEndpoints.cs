using KaungMcp.Clients;
using KaungMcp.Capabilities;
using KaungMcp.Data;
using KaungMcp.Web;
using Microsoft.EntityFrameworkCore;
using KaungMcp.Configuration;
using KaungMcp.InvocationLogs;
using KaungMcp.Security;
using KaungMcp.Scheduling;
using KaungMcp.Tools;
using KaungMcp.Plugins;

namespace KaungMcp.Api;

public static class AdminEndpoints
{
    private static string? OwnerAlias(string? ownerId, string? pluginId)
    {
        if (!string.IsNullOrWhiteSpace(ownerId) && !string.IsNullOrWhiteSpace(pluginId) && ownerId != pluginId) throw new ArgumentException("ownerId 与 pluginId 不一致。");
        return ownerId ?? pluginId;
    }
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin")
            .RequireAuthorization(SecurityConstants.AdminPolicy)
            .WithTags("MCP administration");

        group.MapGet("/settings/definitions", (SettingsDefinitionService definitions, ManagedSettingsStore settings) =>
            definitions.Definitions().Select(s => new { s.OwnerId, s.OwnerKind, s.OwnerName, s.Definition, baselineValue = settings.BaselineValue(s.Definition.Key) }));
        group.MapGet("/capability-owners", async (CapabilityRegistry capabilities, IDbContextFactory<KaungMcpDbContext> database, CancellationToken ct) =>
        { await using var db = await database.CreateDbContextAsync(ct); return await capabilities.CatalogAsync(db, ct); });
        group.MapGet("/modules/web", async (McpToolManagementService tools, PluginRuntime plugins, CancellationToken ct) => new
        { id = "web", ownerKind = "module", name = "网页能力", version = "1.0.0", status = "ready", legacyPackagePresent = plugins.LegacyWebPackagePresent,
          tools = (await tools.GetCatalogAsync(ct)).Categories.SelectMany(c => c.Tools).Where(t => t.OwnerId == "web").OrderBy(t => t.Name == "kaung_web_search" ? 0 : 1) });
        group.MapGet("/settings", (ManagedSettingsStore settings) => settings.Snapshot());
        group.MapPost("/settings/validate", (SettingsChange request, ManagedSettingsStore settings, CancellationToken ct) =>
            settings.ValidateChangeAsync(request, ct));
        group.MapPut("/settings", (SettingsChange request, ManagedSettingsStore settings, CancellationToken ct) =>
            settings.UpdateAsync(request, ct));
        group.MapPost("/admin-key/rotate", async (RotateAdminKeyRequest request, ManagedSettingsStore settings, CancellationToken ct) =>
        {
            await settings.RotateAdminKeyAsync(request.NewKey, ct);
            return Results.Ok(new { rotated = true });
        });
        group.MapGet("/certificate", (ManagedSettingsStore settings) =>
        {
            try { return Results.File(settings.PublicCertificate(), "application/x-x509-ca-cert", "kaungmcp-lan.cer"); }
            catch (FileNotFoundException) { return Results.NotFound(); }
        });

        group.MapGet("/tools", (McpToolManagementService service, CancellationToken cancellationToken) =>
            service.GetCatalogAsync(cancellationToken));

        group.MapGet("/plugins", (PluginRuntime runtime, IServiceProvider services, CancellationToken ct) => runtime.CatalogAsync(services, ct));
        group.MapPut("/plugins/{id}/enabled", async (string id, SetMcpToolEnabledRequest request, PluginRuntime runtime,
            SchedulerService scheduler, IServiceProvider services, CancellationToken ct) =>
        {
            if (!await runtime.SetEnabledAsync(id, request.IsEnabled, services, ct)) return Results.NotFound();
            await scheduler.RefreshPluginAsync(id, ct);
            return Results.Ok(new { id, request.IsEnabled });
        });
        group.MapGet("/plugins/{id}/task-definitions", (string id, PluginRuntime runtime) => runtime.Definitions(id));
        group.MapGet("/scheduled-tasks", (int? page, int? pageSize, string? pluginId, string? ownerId, string? status,
            SchedulerService scheduler, CancellationToken ct) => scheduler.ListAsync(page ?? 1, pageSize ?? 20, OwnerAlias(ownerId, pluginId), status, ct));
        group.MapPost("/scheduled-tasks/preview", (ScheduledTaskChange request, Guid? planId, SchedulerService scheduler, CancellationToken ct) => scheduler.PreviewAsync(request, planId, ct));
        group.MapPost("/scheduled-tasks", async (ScheduledTaskChange request, SchedulerService scheduler, CancellationToken ct) =>
        {
            var task = await scheduler.CreateAsync(request, ct);
            return Results.Created($"/api/admin/scheduled-tasks/{task.Id}", task);
        });
        group.MapGet("/scheduled-tasks/{id:guid}", async (Guid id, SchedulerService scheduler, CancellationToken ct) =>
            await scheduler.GetAsync(id, ct) is { } task ? Results.Ok(task) : Results.NotFound());
        group.MapPut("/scheduled-tasks/{id:guid}", async (Guid id, ScheduledTaskChange request, SchedulerService scheduler, CancellationToken ct) =>
            await scheduler.UpdateAsync(id, request, ct) is { } task ? Results.Ok(task) : Results.NotFound());
        group.MapDelete("/scheduled-tasks/{id:guid}", async (Guid id, SchedulerService scheduler, CancellationToken ct) =>
            await scheduler.SetStateAsync(id, "delete", ct) is { } task ? Results.Ok(task) : Results.NotFound());
        foreach (var action in new[] { "enable", "disable" })
        {
            var operation = action;
            group.MapPost($"/scheduled-tasks/{{id:guid}}/{operation}", async (Guid id, SchedulerService scheduler, CancellationToken ct) =>
                await scheduler.SetStateAsync(id, operation, ct) is { } task ? Results.Ok(task) : Results.NotFound());
        }
        group.MapPost("/scheduled-tasks/{id:guid}/run", async (Guid id, SchedulerService scheduler, CancellationToken ct) =>
        {
            var runId = await scheduler.SubmitAsync(id, "manual", ct);
            return Results.Accepted($"/api/admin/task-runs/{runId}", new { runId });
        });
        group.MapGet("/scheduled-tasks/{id:guid}/runs", (Guid id, int? page, int? pageSize, SchedulerService scheduler, CancellationToken ct) =>
            scheduler.RunsAsync(id, null, page ?? 1, pageSize ?? 20, ct));
        group.MapGet("/task-runs", (string? pluginId, string? ownerId, string? ownerKind, Guid? taskId, string? status, string? trigger, DateTimeOffset? fromUtc, DateTimeOffset? toUtc, string? correlationId, int? page, int? pageSize, SchedulerService scheduler, CancellationToken ct) =>
            scheduler.RunsAsync(taskId, OwnerAlias(ownerId, pluginId), page ?? 1, pageSize ?? 20, ct, status, trigger, fromUtc, toUtc, ownerKind, correlationId));
        group.MapGet("/task-runs/{id:guid}", async (Guid id, SchedulerService scheduler, CancellationToken ct) =>
            await scheduler.RunDetailAsync(id, ct) is { } run ? Results.Ok(run) : Results.NotFound());
        group.MapPost("/task-runs/{id:guid}/cancel", async (Guid id, SchedulerService scheduler, CancellationToken ct) =>
            await scheduler.CancelAsync(id, ct) is { } run ? Results.Ok(run) : Results.NotFound());

        group.MapPut("/tools/{name}/enabled", async (
            string name,
            SetMcpToolEnabledRequest request,
            McpToolManagementService service,
            SchedulerService scheduler,
            CancellationToken cancellationToken) =>
        {
            var state = await service.SetEnabledAsync(name, request.IsEnabled, cancellationToken);
            if (state is not null)
            {
                var owner = (await service.GetCatalogAsync(cancellationToken)).Categories.SelectMany(c => c.Tools).FirstOrDefault(t => t.Name == name)?.OwnerId;
                if (owner is not null) await scheduler.RefreshPluginAsync(owner, cancellationToken, name);
            }
            return state is null ? Results.NotFound() : Results.Ok(new { name, isEnabled = state.Value });
        });

        group.MapPost("/tools/{name}/test", async (
            string name,
            TestMcpToolRequest request,
            McpToolManagementService service,
            CancellationToken cancellationToken) =>
        {
            if (request.Arguments.ValueKind != System.Text.Json.JsonValueKind.Object)
                return Results.BadRequest(new { detail = "Arguments must be a JSON object." });
            try
            {
                var result = await service.TestAsync(name, request.Arguments, request.ConfirmRisk, cancellationToken);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (RiskConfirmationRequiredException exception)
            {
                return Results.Conflict(new { detail = exception.Message, code = "risk_confirmation_required" });
            }
        });

        group.MapPost("/clients", async (
            CreateClientRequest request,
            McpClientApplicationService service,
            CancellationToken cancellationToken) =>
        {
            var client = await service.CreateAsync(request, cancellationToken);
            return Results.Created($"/api/admin/clients/{client.Id}", client);
        });

        group.MapGet("/clients", (
            int? page,
            int? pageSize,
            bool? includeDeleted,
            McpClientApplicationService service,
            CancellationToken cancellationToken) =>
            service.ListAsync(page ?? 1, pageSize ?? 20, includeDeleted ?? false, cancellationToken));

        group.MapPost("/clients/{clientId:guid}/enable", (
            Guid clientId,
            McpClientApplicationService service,
            CancellationToken cancellationToken) =>
            service.EnableAsync(clientId, cancellationToken));

        group.MapPost("/clients/{clientId:guid}/disable", (
            Guid clientId,
            McpClientApplicationService service,
            CancellationToken cancellationToken) =>
            service.DisableAsync(clientId, cancellationToken));

        group.MapPost("/clients/{clientId:guid}/rotate-key", (
            Guid clientId,
            McpClientApplicationService service,
            CancellationToken cancellationToken) =>
            service.RotateKeyAsync(clientId, cancellationToken));

        group.MapDelete("/clients/{clientId:guid}", async (
            Guid clientId,
            McpClientApplicationService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeleteAsync(clientId, cancellationToken);
            return Results.NoContent();
        });

        group.MapGet("/call-logs", (
            Guid? clientId,
            string? toolName,
            string? method,
            string? source,
            string? ownerId, string? ownerKind, string? correlationId,
            bool? succeeded,
            DateTimeOffset? fromUtc,
            DateTimeOffset? toUtc,
            int? page,
            int? pageSize,
            McpInvocationLogService service,
            CancellationToken cancellationToken) =>
            service.ListAsync(
                clientId,
                toolName,
                method,
                source,
                succeeded,
                fromUtc,
                toUtc,
                page ?? 1,
                pageSize ?? 20,
                cancellationToken, ownerId, ownerKind, correlationId));

        group.MapGet("/call-logs/{id:long}", async (
            long id, McpInvocationLogService service, CancellationToken cancellationToken) =>
        {
            var detail = await service.GetAsync(id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        return endpoints;
    }
}
