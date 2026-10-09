using System.Text.Json;
using KaungAgent.Chat;
using KaungAgent.Mcp;
using KaungAgent.Models;
using KaungAgent.Security;

namespace KaungAgent.Api;

public static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api")
            .RequireAuthorization(AgentSecurityConstants.Policy)
            .WithTags("Agent workspace");

        var models = api.MapGroup("/models");
        models.MapGet("/", (ModelProfileService service, CancellationToken ct) => service.ListAsync(ct));
        models.MapPost("/", (SaveModelProfileRequest request, ModelProfileService service, CancellationToken ct) =>
            service.SaveAsync(null, request, ct));
        models.MapPut("/{id:guid}", (Guid id, SaveModelProfileRequest request, ModelProfileService service, CancellationToken ct) =>
            service.SaveAsync(id, request, ct));
        models.MapDelete("/{id:guid}", async (Guid id, ModelProfileService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        models.MapPost("/{id:guid}/default", async (Guid id, ModelProfileService service, CancellationToken ct) =>
        {
            await service.SetDefaultAsync(id, ct);
            return Results.NoContent();
        });
        models.MapPost("/{id:guid}/test", (Guid id, ModelProfileService service, CancellationToken ct) => service.TestAsync(id, ct));

        var mcp = api.MapGroup("/mcp/servers");
        mcp.MapGet("/", (McpRegistryService service, CancellationToken ct) => service.ListAsync(ct));
        mcp.MapGet("/{id:guid}", async (Guid id, McpRegistryService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());
        mcp.MapPost("/", (SaveMcpServerRequest request, McpRegistryService service, CancellationToken ct) =>
            service.SaveAsync(null, request, ct));
        mcp.MapPut("/{id:guid}", (Guid id, SaveMcpServerRequest request, McpRegistryService service, CancellationToken ct) =>
            service.SaveAsync(id, request, ct));
        mcp.MapDelete("/{id:guid}", async (Guid id, McpRegistryService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        mcp.MapPost("/{id:guid}/enable", async (Guid id, McpRegistryService service, CancellationToken ct) =>
        {
            await service.SetEnabledAsync(id, true, ct);
            return Results.NoContent();
        });
        mcp.MapPost("/{id:guid}/disable", async (Guid id, McpRegistryService service, CancellationToken ct) =>
        {
            await service.SetEnabledAsync(id, false, ct);
            return Results.NoContent();
        });
        mcp.MapPost("/{id:guid}/refresh", (Guid id, McpRegistryService service, CancellationToken ct) => service.RefreshAsync(id, ct));
        mcp.MapPost("/{id:guid}/tools/{name}/invoke", (Guid id, string name, JsonElement body, McpRegistryService service, CancellationToken ct) =>
            service.InvokeToolAsync(id, name, ToArguments(body), ct));
        mcp.MapPost("/{id:guid}/prompts/{name}/get", (Guid id, string name, JsonElement body, McpRegistryService service, CancellationToken ct) =>
            service.GetPromptAsync(id, name, ToArguments(body), ct));
        mcp.MapPost("/{id:guid}/resources/read", (Guid id, ResourceReadRequest request, McpRegistryService service, CancellationToken ct) =>
            service.ReadResourceAsync(id, request.Uri, request.Arguments, ct));

        var chats = api.MapGroup("/conversations");
        chats.MapGet("/", (ConversationService service, CancellationToken ct) => service.ListAsync(ct));
        chats.MapGet("/{id:guid}", async (Guid id, ConversationService service, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } item ? Results.Ok(item) : Results.NotFound());
        chats.MapPost("/", (CreateConversationRequest request, ConversationService service, CancellationToken ct) =>
            service.CreateAsync(request.ModelProfileId, request.ToolSelections, ct));
        chats.MapPut("/{id:guid}/title", async (Guid id, RenameConversationRequest request, ConversationService service, CancellationToken ct) =>
        {
            await service.RenameAsync(id, request.Title, ct);
            return Results.NoContent();
        });
        chats.MapDelete("/{id:guid}", async (Guid id, ConversationService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        chats.MapPost("/{id:guid}/messages", async (
            Guid id, SendMessageRequest request, ChatOrchestrator orchestrator, CancellationToken ct) =>
        {
            var events = new List<ChatStreamEvent>();
            await orchestrator.SendAsync(id, request.Content, item => { events.Add(item); return Task.CompletedTask; }, ct);
            return Results.Ok(events);
        });

        return endpoints;
    }

    private static IReadOnlyDictionary<string, object?> ToArguments(JsonElement element)
    {
        var source = element.TryGetProperty("arguments", out var nested) ? nested : element;
        if (source.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments 必须是 JSON 对象。");
        return source.EnumerateObject().ToDictionary(item => item.Name, item => (object?)item.Value.Clone());
    }
}

public sealed record ResourceReadRequest(string Uri, IReadOnlyDictionary<string, object?>? Arguments);
public sealed record CreateConversationRequest(Guid? ModelProfileId, IReadOnlyList<ConversationToolSelection>? ToolSelections);
public sealed record RenameConversationRequest(string Title);
public sealed record SendMessageRequest(string Content);
