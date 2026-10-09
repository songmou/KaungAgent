using System.Text.Json;
using KaungAgent.Data;
using KaungAgent.Data.Entities;
using KaungAgent.Models;
using Microsoft.EntityFrameworkCore;

namespace KaungAgent.Chat;

public sealed class ConversationService(
    IDbContextFactory<KaungAgentDbContext> dbFactory,
    ModelProfileService modelProfiles)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var items = await db.Conversations.AsNoTracking()
            .Select(item => new ConversationSummary(item.Id, item.Title, item.ModelProfileId, item.UpdatedAtUtc, item.Messages.Count))
            .ToListAsync(cancellationToken);
        return items.OrderByDescending(item => item.UpdatedAtUtc).ToList();
    }

    public async Task<ConversationDetail?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Conversations.AsNoTracking()
            .Include(item => item.Messages.OrderBy(message => message.Id))
            .ThenInclude(message => message.ToolExecutions)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return entity is null ? null : ToDetail(entity);
    }

    public async Task<ConversationDetail> CreateAsync(
        Guid? modelProfileId = null,
        IReadOnlyList<ConversationToolSelection>? selections = null,
        CancellationToken cancellationToken = default)
    {
        var model = modelProfileId is null
            ? await modelProfiles.GetDefaultEntityAsync(cancellationToken)
            : await modelProfiles.GetEntityAsync(modelProfileId.Value, cancellationToken);
        if (model is null || !model.IsEnabled) throw new InvalidOperationException("请先配置并启用一个默认模型。");

        if (selections is null)
        {
            await using var lookup = await dbFactory.CreateDbContextAsync(cancellationToken);
            selections = await lookup.McpServers.AsNoTracking().Where(item => item.IsEnabled)
                .Select(item => new ConversationToolSelection(item.Id, null)).ToListAsync(cancellationToken);
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new Conversation
        {
            Id = Guid.NewGuid(), Title = "新对话", ModelProfileId = model.Id,
            ToolSelectionJson = JsonSerializer.Serialize(selections, JsonOptions), CreatedAtUtc = now, UpdatedAtUtc = now
        };
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Conversations.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return ToDetail(entity);
    }

    public async Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("标题不能为空。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Conversations.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("会话不存在。");
        entity.Title = title.Trim()[..Math.Min(title.Trim().Length, 200)];
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateSettingsAsync(
        Guid id, Guid modelProfileId, IReadOnlyList<ConversationToolSelection> selections, CancellationToken cancellationToken = default)
    {
        var model = await modelProfiles.GetEntityAsync(modelProfileId, cancellationToken);
        if (model is null || !model.IsEnabled) throw new InvalidOperationException("所选模型不存在或已禁用。");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Conversations.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("会话不存在。");
        entity.ModelProfileId = modelProfileId;
        entity.ToolSelectionJson = JsonSerializer.Serialize(selections, JsonOptions);
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Conversations.FindAsync([id], cancellationToken) ?? throw new KeyNotFoundException("会话不存在。");
        db.Conversations.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ChatMessage> AddMessageAsync(
        Guid conversationId, string role, string? content, string? status = "completed", CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await db.Conversations.FindAsync([conversationId], cancellationToken)
            ?? throw new KeyNotFoundException("会话不存在。");
        var now = DateTimeOffset.UtcNow;
        var message = new ChatMessage
        {
            ConversationId = conversationId, Role = role, Content = content, Status = status, CreatedAtUtc = now
        };
        db.ChatMessages.Add(message);
        conversation.UpdatedAtUtc = now;
        if (role == "user" && conversation.Title == "新对话" && !string.IsNullOrWhiteSpace(content))
            conversation.Title = content.Trim()[..Math.Min(content.Trim().Length, 32)];
        await db.SaveChangesAsync(cancellationToken);
        return message;
    }

    private static ConversationDetail ToDetail(Conversation entity) => new(
        entity.Id, entity.Title, entity.ModelProfileId,
        JsonSerializer.Deserialize<List<ConversationToolSelection>>(entity.ToolSelectionJson, JsonOptions) ?? [],
        entity.Messages.OrderBy(item => item.Id).Select(message => new ChatMessageResponse(
            message.Id, message.Role, message.Content, message.ToolName, message.Status, message.Error,
            message.InputTokens, message.OutputTokens, message.TotalTokens, message.DurationMilliseconds, message.CreatedAtUtc,
            message.ToolExecutions.OrderBy(item => item.Id).Select(execution => new ToolExecutionResponse(
                execution.Id, execution.McpServerId, execution.ToolCallId, execution.ToolName,
                execution.ArgumentsJson, execution.ResultJson, execution.Succeeded, execution.Error, execution.DurationMilliseconds)).ToList())).ToList(),
        entity.UpdatedAtUtc);
}
