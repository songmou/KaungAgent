using KaungAgent.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KaungAgent.Data;

public sealed class KaungAgentDbContext(DbContextOptions<KaungAgentDbContext> options) : DbContext(options)
{
    public DbSet<ModelProfile> ModelProfiles => Set<ModelProfile>();
    public DbSet<McpServerRegistration> McpServers => Set<McpServerRegistration>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ToolExecution> ToolExecutions => Set<ToolExecution>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ModelProfile>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(120);
            entity.Property(item => item.BaseUrl).HasMaxLength(2048);
            entity.Property(item => item.ModelId).HasMaxLength(200);
            entity.HasIndex(item => item.Name).IsUnique();
            entity.HasIndex(item => item.IsDefault);
        });

        modelBuilder.Entity<McpServerRegistration>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Name).HasMaxLength(120);
            entity.Property(item => item.Endpoint).HasMaxLength(2048);
            entity.Property(item => item.ManagedKey).HasMaxLength(120);
            entity.HasIndex(item => item.Name).IsUnique();
            entity.HasIndex(item => item.ManagedKey).IsUnique();
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Title).HasMaxLength(200);
            entity.HasOne(item => item.ModelProfile)
                .WithMany()
                .HasForeignKey(item => item.ModelProfileId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(item => item.UpdatedAtUtc);
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Role).HasMaxLength(32);
            entity.Property(item => item.Status).HasMaxLength(32);
            entity.HasOne(item => item.Conversation)
                .WithMany(item => item.Messages)
                .HasForeignKey(item => item.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(item => new { item.ConversationId, item.Id });
            entity.HasIndex(item => new { item.ConversationId, item.Status, item.Id });
        });

        modelBuilder.Entity<ToolExecution>(entity =>
        {
            entity.HasKey(item => item.Id);
            entity.Property(item => item.ToolCallId).HasMaxLength(200);
            entity.Property(item => item.ToolName).HasMaxLength(300);
            entity.HasOne(item => item.ChatMessage)
                .WithMany(item => item.ToolExecutions)
                .HasForeignKey(item => item.ChatMessageId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
