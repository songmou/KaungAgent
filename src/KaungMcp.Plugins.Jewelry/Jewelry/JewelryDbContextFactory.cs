using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
namespace KaungMcp.Jewelry;

public sealed class JewelryDbContextFactory : IDesignTimeDbContextFactory<JewelryDbContext>
{
    public JewelryDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<JewelryDbContext>().UseSqlite("Data Source=jewelry.design.db").Options);
}
