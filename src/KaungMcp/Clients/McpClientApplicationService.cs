using KaungMcp.Data;
using KaungMcp.Data.Entities;
using KaungMcp.Security;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.Clients;

public sealed class McpClientApplicationService(
    IDbContextFactory<KaungMcpDbContext> dbContextFactory)
{
    public async Task<CreatedClientResponse> CreateAsync(
        CreateClientRequest request,
        CancellationToken cancellationToken)
    {
        var name = ValidateName(request.Name);
        var description = ValidateDescription(request.Description);

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        if (await dbContext.ClientApplications.AnyAsync(item => item.Name == name, cancellationToken))
        {
            throw new ClientApplicationConflictException($"An MCP client named '{name}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        var generated = ApiKeyCodec.Generate();
        var client = new McpClientApplication
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            KeyId = generated.KeyId,
            KeyHash = generated.Hash,
            IsEnabled = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        dbContext.ClientApplications.Add(client);
        await dbContext.SaveChangesAsync(cancellationToken);
        return new CreatedClientResponse(
            client.Id,
            client.Name,
            client.Description,
            client.IsEnabled,
            generated.PlainText,
            client.CreatedAtUtc);
    }

    public async Task<PagedResponse<ClientResponse>> ListAsync(
        int page,
        int pageSize,
        bool includeDeleted,
        CancellationToken cancellationToken)
    {
        (page, pageSize) = NormalizePage(page, pageSize);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var query = dbContext.ClientApplications.AsNoTracking();
        if (!includeDeleted)
        {
            query = query.Where(item => item.DeletedAtUtc == null);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(item => item.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new ClientResponse(
                item.Id,
                item.Name,
                item.Description,
                item.KeyId,
                item.IsEnabled,
                item.CreatedAtUtc,
                item.UpdatedAtUtc,
                item.LastUsedAtUtc,
                item.DeletedAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedResponse<ClientResponse>(items, page, pageSize, totalCount);
    }

    public Task<ClientResponse> EnableAsync(Guid clientId, CancellationToken cancellationToken) =>
        SetEnabledAsync(clientId, true, cancellationToken);

    public Task<ClientResponse> DisableAsync(Guid clientId, CancellationToken cancellationToken) =>
        SetEnabledAsync(clientId, false, cancellationToken);

    public async Task<RotatedClientKeyResponse> RotateKeyAsync(Guid clientId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var client = await GetActiveRecordAsync(dbContext, clientId, cancellationToken);
        var generated = ApiKeyCodec.Generate();
        var now = DateTimeOffset.UtcNow;
        client.KeyId = generated.KeyId;
        client.KeyHash = generated.Hash;
        client.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return new RotatedClientKeyResponse(client.Id, generated.PlainText, now);
    }

    public async Task DeleteAsync(Guid clientId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var client = await GetActiveRecordAsync(dbContext, clientId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        client.IsEnabled = false;
        client.DeletedAtUtc = now;
        client.UpdatedAtUtc = now;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task<ClientResponse> SetEnabledAsync(
        Guid clientId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var client = await GetActiveRecordAsync(dbContext, clientId, cancellationToken);
        client.IsEnabled = isEnabled;
        client.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToResponse(client);
    }

    private static async Task<McpClientApplication> GetActiveRecordAsync(
        KaungMcpDbContext dbContext,
        Guid clientId,
        CancellationToken cancellationToken)
    {
        var client = await dbContext.ClientApplications.SingleOrDefaultAsync(
            item => item.Id == clientId && item.DeletedAtUtc == null,
            cancellationToken);
        return client ?? throw new ClientApplicationNotFoundException(clientId);
    }

    private static ClientResponse ToResponse(McpClientApplication item) =>
        new(
            item.Id,
            item.Name,
            item.Description,
            item.KeyId,
            item.IsEnabled,
            item.CreatedAtUtc,
            item.UpdatedAtUtc,
            item.LastUsedAtUtc,
            item.DeletedAtUtc);

    private static string ValidateName(string name)
    {
        var normalized = name?.Trim() ?? string.Empty;
        if (normalized.Length is < 2 or > 100)
        {
            throw new ArgumentException("Client name must contain between 2 and 100 characters.", nameof(name));
        }

        return normalized;
    }

    private static string? ValidateDescription(string? description)
    {
        var normalized = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (normalized?.Length > 500)
        {
            throw new ArgumentException("Client description cannot exceed 500 characters.", nameof(description));
        }

        return normalized;
    }

    private static (int Page, int PageSize) NormalizePage(int page, int pageSize) =>
        (Math.Max(page, 1), Math.Clamp(pageSize, 1, 100));
}
