using KaungMcp.Configuration;
using KaungMcp.Plugin;
using KaungMcp.Security;
using Microsoft.AspNetCore.Http.Features;

namespace KaungMcp.Content;

public static class ContentEndpoints
{
    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapFiles(endpoints.MapGroup("/api/files").RequireAuthorization(SecurityConstants.McpClientPolicy));
        MapFiles(endpoints.MapGroup("/api/admin/files").RequireAuthorization(SecurityConstants.AdminPolicy));
        var jobs = endpoints.MapGroup("/api/admin/content/jobs").RequireAuthorization(SecurityConstants.AdminPolicy);
        jobs.MapGet("/{id:guid}", (Guid id, IPluginJobService service, CancellationToken ct) => service.GetJobAsync(id, ct));
        jobs.MapPost("/{id:guid}/cancel", (Guid id, IPluginJobService service, CancellationToken ct) => service.CancelJobAsync(id, ct));
        return endpoints;
    }
    private static void MapFiles(RouteGroupBuilder group)
    {
        group.MapPost("", async (HttpContext context, IContentFileStore files, ManagedSettingsStore settings, CancellationToken ct) =>
        {
            var maximum = settings.GetOptions<ContentOptions>("Content").MaxFileBytes;
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = maximum + 1024 * 1024;
            if (context.Request.ContentLength > maximum + 1024 * 1024) throw new ContentLimitException("文件超过大小限制。");
            if (!context.Request.HasFormContentType) throw new ArgumentException("请使用 multipart/form-data 上传文件。");
            var form = await context.Request.ReadFormAsync(new FormOptions
            {
                MultipartBodyLengthLimit = maximum + 1024 * 1024, MemoryBufferThreshold = 65536,
                ValueLengthLimit = 65536, MultipartHeadersLengthLimit = 16384
            }, ct);
            if (form.Files.Count != 1) throw new ArgumentException("每次上传一个文件。");
            await using var source = form.Files[0].OpenReadStream();
            var file = await files.UploadAsync(source, form.Files[0].FileName, ct);
            return Results.Created($"{context.Request.Path}/{file.FileId}", file);
        });
        group.MapGet("/{id}", (string id, IContentFileStore files, CancellationToken ct) => files.GetAsync(id, ct));
        group.MapGet("/{id}/download", async (string id, HttpContext context, IContentFileStore files, CancellationToken ct) =>
        {
            var info = await files.GetAsync(id, ct);
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.Stream(await files.OpenReadAsync(id, ct), info.MimeType, info.Name);
        });
        group.MapGet("/{id}/text", (string id, int? startIndex, int? maxCharacters, IContentFileStore files, CancellationToken ct) =>
            files.ReadTextAsync(id, startIndex ?? 0, maxCharacters ?? 8000, ct));
        group.MapDelete("/{id}", async (string id, IContentFileStore files, CancellationToken ct) =>
        {
            await files.DeleteAsync(id, ct);
            return Results.NoContent();
        });
    }
}
