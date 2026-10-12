using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KaungMcp.Data;

public sealed class KaungMcpDbContextFactory : IDesignTimeDbContextFactory<KaungMcpDbContext>
{
    public KaungMcpDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<KaungMcpDbContext>()
            .UseSqlite("Data Source=kaungmcp.design.db")
            .Options;
        return new KaungMcpDbContext(options);
    }
}
