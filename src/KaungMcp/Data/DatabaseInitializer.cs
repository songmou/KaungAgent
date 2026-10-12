using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.Data;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(
        IServiceProvider services,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        SQLitePCL.Batteries_V2.Init();

        var connectionBuilder = new SqliteConnectionStringBuilder(connectionString);
        if (!string.IsNullOrWhiteSpace(connectionBuilder.DataSource) &&
            !string.Equals(connectionBuilder.DataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(connectionBuilder.DataSource);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        }

        await using var scope = services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<KaungMcpDbContext>>();
        await using var dbContext = await factory.CreateDbContextAsync(cancellationToken);
        await dbContext.Database.MigrateAsync(cancellationToken);

        var connection = dbContext.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
