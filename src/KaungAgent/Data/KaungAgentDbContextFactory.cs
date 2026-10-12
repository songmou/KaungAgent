using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KaungAgent.Data;

public sealed class KaungAgentDbContextFactory : IDesignTimeDbContextFactory<KaungAgentDbContext>
{
    public KaungAgentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KaungAgentDbContext>()
            .UseSqlite("Data Source=data/kaungagent.db;Cache=Shared;Default Timeout=30")
            .Options;
        return new KaungAgentDbContext(options);
    }
}
