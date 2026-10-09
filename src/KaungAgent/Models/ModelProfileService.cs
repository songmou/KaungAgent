using System.Text.Json;
using KaungAgent.Data;
using KaungAgent.Data.Entities;
using KaungAgent.Security;
using Microsoft.EntityFrameworkCore;

namespace KaungAgent.Models;

public sealed class ModelProfileService(
    IDbContextFactory<KaungAgentDbContext> dbFactory,
    ProtectedSecretStore secrets,
    OpenAiCompatibleClient chatClient)
{
    public const string DeepSeekProfileName = "DeepSeek Flash";
    public const string DeepSeekBaseUrl = "https://api.deepseek.com";
    public const string DeepSeekModelId = "deepseek-flash";

    public async Task<IReadOnlyList<ModelProfileResponse>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.ModelProfiles.AsNoTracking()
            .OrderByDescending(item => item.IsDefault)
            .ThenBy(item => item.Name)
            .ToListAsync(cancellationToken);
        return items.Select(ToResponse).ToList();
    }

    public async Task<ModelProfileResponse?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.ModelProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return item is null ? null : ToResponse(item);
    }

    public async Task<ModelProfile?> GetEntityAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ModelProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
    }

    public async Task<ModelProfile?> GetDefaultEntityAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ModelProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IsDefault && item.IsEnabled, cancellationToken);
    }

    public async Task<ModelProfileResponse> SaveAsync(Guid? id, SaveModelProfileRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        var headers = ParseHeaders(request.HeadersJson);
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = id is null
            ? new ModelProfile { Id = Guid.NewGuid(), Name = "", BaseUrl = "", ModelId = "", CreatedAtUtc = now }
            : await db.ModelProfiles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException("模型配置不存在。");

        if (id is null)
        {
            db.ModelProfiles.Add(entity);
        }

        entity.Name = request.Name.Trim();
        entity.BaseUrl = request.BaseUrl.Trim().TrimEnd('/');
        entity.ModelId = request.ModelId.Trim();
        entity.Temperature = request.Temperature;
        entity.MaxOutputTokens = request.MaxOutputTokens;
        entity.TimeoutSeconds = request.TimeoutSeconds;
        entity.IsEnabled = request.IsEnabled;
        entity.IsDefault = request.IsDefault;
        entity.UpdatedAtUtc = now;

        if (!string.IsNullOrWhiteSpace(request.ApiKey))
        {
            entity.ProtectedApiKey = secrets.Protect(request.ApiKey.Trim());
        }
        if (request.HeadersJson is not null)
        {
            entity.ProtectedHeadersJson = secrets.ProtectHeaders(headers);
        }

        var hasDefault = await db.ModelProfiles.AnyAsync(item => item.Id != entity.Id && item.IsDefault, cancellationToken);
        if (!hasDefault && entity.IsEnabled)
        {
            entity.IsDefault = true;
        }
        if (entity.IsDefault)
        {
            await db.ModelProfiles.Where(item => item.Id != entity.Id && item.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(entity);
    }

    public async Task<ModelProfileResponse?> EnsureDeepSeekAsync(
        string? apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return null;

        var normalizedApiKey = apiKey.Trim();
        var now = DateTimeOffset.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ModelProfiles.SingleOrDefaultAsync(
            item => item.BaseUrl == DeepSeekBaseUrl && item.ModelId == DeepSeekModelId,
            cancellationToken);
        entity ??= await db.ModelProfiles.SingleOrDefaultAsync(
            item => item.Name == DeepSeekProfileName,
            cancellationToken);

        if (entity is null)
        {
            entity = new ModelProfile
            {
                Id = Guid.NewGuid(),
                Name = DeepSeekProfileName,
                BaseUrl = DeepSeekBaseUrl,
                ModelId = DeepSeekModelId,
                Temperature = 0.7,
                MaxOutputTokens = 4096,
                TimeoutSeconds = 120,
                CreatedAtUtc = now
            };
            db.ModelProfiles.Add(entity);
        }

        entity.BaseUrl = DeepSeekBaseUrl;
        entity.ModelId = DeepSeekModelId;
        entity.ProtectedApiKey = secrets.Protect(normalizedApiKey);
        entity.IsEnabled = true;
        entity.UpdatedAtUtc = now;

        var hasOtherEnabledDefault = await db.ModelProfiles.AnyAsync(
            item => item.Id != entity.Id && item.IsDefault && item.IsEnabled,
            cancellationToken);
        if (hasOtherEnabledDefault)
        {
            entity.IsDefault = false;
        }
        else
        {
            await db.ModelProfiles.Where(item => item.Id != entity.Id && item.IsDefault)
                .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), cancellationToken);
            entity.IsDefault = true;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(entity);
    }

    public async Task SetDefaultAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ModelProfiles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("模型配置不存在。");
        if (!entity.IsEnabled) throw new InvalidOperationException("禁用的模型不能设为默认模型。");
        await db.ModelProfiles.Where(item => item.IsDefault)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.IsDefault, false), cancellationToken);
        entity.IsDefault = true;
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.ModelProfiles.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("模型配置不存在。");
        if (await db.Conversations.AnyAsync(item => item.ModelProfileId == id, cancellationToken))
        {
            throw new InvalidOperationException("该模型已有会话引用，请先禁用而不是删除。");
        }
        db.ModelProfiles.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ModelConnectionTestResult> TestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await GetEntityAsync(id, cancellationToken) ?? throw new KeyNotFoundException("模型配置不存在。");
        var started = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await chatClient.CompleteAsync(
                entity,
                [new ChatRequestMessage("user", "请只回复 OK。")],
                [],
                _ => Task.CompletedTask,
                cancellationToken,
                maxOutputTokensOverride: 2);
            return new(true, "连接成功，模型已返回响应。", started.ElapsedMilliseconds);
        }
        catch (Exception exception)
        {
            return new(false, exception.Message, started.ElapsedMilliseconds);
        }
    }

    public (string? ApiKey, IReadOnlyDictionary<string, string> Headers) RevealSecrets(ModelProfile profile) =>
        (secrets.Unprotect(profile.ProtectedApiKey), secrets.UnprotectHeaders(profile.ProtectedHeadersJson));

    private ModelProfileResponse ToResponse(ModelProfile entity) => new(
        entity.Id, entity.Name, entity.BaseUrl, entity.ModelId,
        !string.IsNullOrWhiteSpace(entity.ProtectedApiKey),
        secrets.UnprotectHeaders(entity.ProtectedHeadersJson).Keys.Order().ToList(),
        entity.Temperature, entity.MaxOutputTokens, entity.TimeoutSeconds,
        entity.IsEnabled, entity.IsDefault, entity.UpdatedAtUtc);

    private static Dictionary<string, string> ParseHeaders(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? throw new ArgumentException("自定义请求头必须是 JSON 对象。");
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("自定义请求头必须是字符串键值 JSON 对象。", exception);
        }
    }

    private static void Validate(SaveModelProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) throw new ArgumentException("名称不能为空。");
        if (string.IsNullOrWhiteSpace(request.ModelId)) throw new ArgumentException("模型 ID 不能为空。");
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Base URL 必须是有效的 HTTP/HTTPS 地址。");
        if (request.Temperature is < 0 or > 2) throw new ArgumentException("温度必须在 0 到 2 之间。");
        if (request.MaxOutputTokens is < 1 or > 1_000_000) throw new ArgumentException("最大输出 Token 无效。");
        if (request.TimeoutSeconds is < 1 or > 600) throw new ArgumentException("超时必须在 1 到 600 秒之间。");
    }
}
