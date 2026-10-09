using KaungMcp.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.Security;

public sealed class ApiKeyValidator(
    IDbContextFactory<KaungMcpDbContext> dbContextFactory,
    ILogger<ApiKeyValidator> logger) : IApiKeyValidator
{
    public async Task<ValidatedClient?> ValidateAsync(string apiKey, CancellationToken cancellationToken)
    {
        if (!ApiKeyCodec.TryGetKeyId(apiKey, out var keyId))
        {
            return null;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var client = await dbContext.ClientApplications
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.KeyId == keyId && item.IsEnabled && item.DeletedAtUtc == null,
                cancellationToken);

        if (client is null || !ApiKeyCodec.Verify(apiKey, client.KeyHash))
        {
            return null;
        }

        try
        {
            await dbContext.ClientApplications
                .Where(item => item.Id == client.Id)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(item => item.LastUsedAtUtc, DateTimeOffset.UtcNow),
                    cancellationToken);
        }
        catch (Exception exception) when (exception is DbUpdateException or SqliteException)
        {
            logger.LogWarning(exception, "Unable to update last-used time for MCP client {ClientId}.", client.Id);
        }

        return new ValidatedClient(client.Id, client.Name);
    }
}
