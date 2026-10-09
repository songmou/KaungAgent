using KaungMcp.Clients;
using KaungMcp.Configuration;
using KaungMcp.InvocationLogs;
using KaungMcp.Security;
using KaungMcp.Scheduling;
using KaungMcp.Tools;

namespace KaungMcp.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin")
            .RequireAuthorization(SecurityConstants.AdminPolicy)
            .WithTags("MCP administration");

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

        group.MapGet("/scheduler/catalog", (SchedulerService scheduler, CancellationToken ct) =>
            scheduler.CatalogAsync(ct));
        group.MapPost("/scheduler/validate-schedule", (ScheduleValidationRequest request, SchedulerService scheduler,
            TimeProvider clock) => SchedulerService.ValidateSchedule(request, clock.GetUtcNow()));
        group.MapGet("/scheduled-tasks", (int? page, int? pageSize, string? status, string? executionType,
            string? target, SchedulerService scheduler, CancellationToken ct) =>
            scheduler.ListAsync(page ?? 1, pageSize ?? 20, status, executionType, target, ct));
        group.MapPost("/scheduled-tasks", async (ScheduledTaskChange request, SchedulerService scheduler,
            CancellationToken ct) =>
        {
            var task = await scheduler.CreateAsync(request, ct);
            return Results.Created($"/api/admin/scheduled-tasks/{task.Id}", task);
        });
        group.MapGet("/scheduled-tasks/{id:guid}", async (Guid id, SchedulerService scheduler, CancellationToken ct) =>
            await scheduler.GetAsync(id, ct) is { } task ? Results.Ok(task) : Results.NotFound());
        group.MapPut("/scheduled-tasks/{id:guid}", async (Guid id, ScheduledTaskChange request,
            SchedulerService scheduler, CancellationToken ct) =>
            await scheduler.UpdateAsync(id, request, ct) is { } task ? Results.Ok(task) : Results.NotFound());
        group.MapDelete("/scheduled-tasks/{id:guid}", async (Guid id, SchedulerService scheduler, CancellationToken ct) =>
            await scheduler.SetStateAsync(id, "archive", ct) is { } task ? Results.Ok(task) : Results.NotFound());
        foreach (var action in new[] { "enable", "disable", "restore" })
        {
            var operation = action;
            group.MapPost($"/scheduled-tasks/{{id:guid}}/{operation}", async (Guid id,
                SchedulerService scheduler, CancellationToken ct) =>
                await scheduler.SetStateAsync(id, operation, ct) is { } task ? Results.Ok(task) : Results.NotFound());
        }
        group.MapPost("/scheduled-tasks/{id:guid}/run", async (Guid id, SchedulerService scheduler,
            CancellationToken ct) =>
            await scheduler.RunAsync(id, "manual", true, ct) is { } run ? Results.Ok(run) : Results.NotFound());
        group.MapGet("/scheduled-tasks/{id:guid}/runs", (Guid id, int? page, int? pageSize,
            SchedulerService scheduler, CancellationToken ct) =>
            scheduler.RunsAsync(id, page ?? 1, pageSize ?? 20, ct));
        group.MapGet("/scheduled-task-runs/{runId:long}", async (long runId, SchedulerService scheduler,
            CancellationToken ct) =>
            await scheduler.RunDetailAsync(runId, ct) is { } run ? Results.Ok(run) : Results.NotFound());

        group.MapPut("/tools/{name}/enabled", async (
            string name,
            SetMcpToolEnabledRequest request,
            McpToolManagementService service,
            CancellationToken cancellationToken) =>
        {
            var state = await service.SetEnabledAsync(name, request.IsEnabled, cancellationToken);
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
                cancellationToken));

        group.MapGet("/call-logs/{id:long}", async (
            long id, McpInvocationLogService service, CancellationToken cancellationToken) =>
        {
            var detail = await service.GetAsync(id, cancellationToken);
            return detail is null ? Results.NotFound() : Results.Ok(detail);
        });

        return endpoints;
    }
}
