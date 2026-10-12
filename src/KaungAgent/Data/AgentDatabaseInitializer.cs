using Microsoft.EntityFrameworkCore;

namespace KaungAgent.Data;

public static class AgentDatabaseInitializer
{
    public static async Task InitializeAsync(IServiceProvider services, string connectionString)
    {
        var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(builder.DataSource) && builder.DataSource != ":memory:")
        {
            var path = Path.GetFullPath(builder.DataSource, Directory.GetCurrentDirectory());
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KaungAgentDbContext>>();
        await using var dbContext = await factory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await dbContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
    }
}
