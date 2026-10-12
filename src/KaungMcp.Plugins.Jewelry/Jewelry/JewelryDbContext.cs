using Microsoft.EntityFrameworkCore;
namespace KaungMcp.Jewelry;

public sealed class JewelryDbContext(DbContextOptions<JewelryDbContext> options) : DbContext(options)
{
    public DbSet<JewelrySourceEntity> JewelrySources => Set<JewelrySourceEntity>();
    public DbSet<JewelryCollectionState> JewelryCollectionStates => Set<JewelryCollectionState>();
    public DbSet<JewelryCollectionAttempt> JewelryCollectionAttempts => Set<JewelryCollectionAttempt>();
    public DbSet<JewelryQuoteEntity> JewelryQuotes => Set<JewelryQuoteEntity>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var source = modelBuilder.Entity<JewelrySourceEntity>();
        source.ToTable("JewelrySources");
        source.HasKey(x => x.Id);
        var state = modelBuilder.Entity<JewelryCollectionState>();
        state.ToTable("JewelryCollectionStates");
        state.HasKey(x => x.SourceId);
        state.HasOne<JewelrySourceEntity>().WithOne().HasForeignKey<JewelryCollectionState>(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        var attempt = modelBuilder.Entity<JewelryCollectionAttempt>();
        attempt.ToTable("JewelryCollectionAttempts");
        attempt.HasKey(x => x.Id);
        attempt.HasIndex(x => new { x.SourceId, x.StartedTicks });
        attempt.HasOne<JewelrySourceEntity>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
        var quote = modelBuilder.Entity<JewelryQuoteEntity>();
        quote.ToTable("JewelryQuotes");
        quote.HasKey(x => x.Id);
        quote.HasIndex(x => new { x.SeriesKey, x.SourceDate, x.CollectedTicks });
        quote.HasIndex(x => new { x.InstrumentId, x.SourceDate, x.CollectedTicks });
        quote.HasIndex(x => new { x.InstrumentId, x.CollectedTicks });
        quote.HasOne<JewelrySourceEntity>().WithMany().HasForeignKey(x => x.SourceId).OnDelete(DeleteBehavior.Restrict);
    }
}
