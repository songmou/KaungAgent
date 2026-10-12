using KaungMcp.Data.Entities;
using KaungMcp.InvocationLogs;
using KaungMcp.Scheduling;
using KaungMcp.Plugins;
using Microsoft.EntityFrameworkCore;

namespace KaungMcp.Data;

public sealed class KaungMcpDbContext(DbContextOptions<KaungMcpDbContext> options) : DbContext(options)
{
    public DbSet<McpClientApplication> ClientApplications => Set<McpClientApplication>();

    public DbSet<McpInvocationLog> InvocationLogs => Set<McpInvocationLog>();
    public DbSet<McpToolSetting> ToolSettings => Set<McpToolSetting>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<TaskRun> TaskRuns => Set<TaskRun>();
    public DbSet<PluginState> PluginStates => Set<PluginState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var scheduledTask = modelBuilder.Entity<ScheduledTask>();
        scheduledTask.ToTable("ScheduledTasks");
        scheduledTask.HasKey(x => x.Id);
        scheduledTask.Property(x => x.Name).HasMaxLength(120).IsRequired();
        scheduledTask.Property(x => x.TargetId).HasMaxLength(200).IsRequired();
        scheduledTask.Property(x => x.TargetType).HasMaxLength(32).IsRequired();
        scheduledTask.Property(x => x.ScheduleType).HasMaxLength(16).IsRequired();
        scheduledTask.Property(x => x.TimeZoneId).HasMaxLength(100).IsRequired();
        scheduledTask.HasIndex(x => new { x.IsEnabled, x.IsDeleted, x.NextRunTicks });
        var scheduledRun = modelBuilder.Entity<TaskRun>();
        scheduledRun.ToTable("TaskRuns");
        scheduledRun.HasKey(x => x.Id);
        scheduledRun.Property(x => x.Trigger).HasMaxLength(32).IsRequired();
        scheduledRun.Property(x => x.Status).HasMaxLength(32).IsRequired();
        scheduledRun.HasIndex(x => new { x.TaskId, x.StartedTicks });
        scheduledRun.HasOne<ScheduledTask>().WithMany().HasForeignKey(x => x.TaskId).OnDelete(DeleteBehavior.Restrict);
        scheduledTask.HasIndex(x => new { x.PluginId, x.TemplateId }).IsUnique();
        scheduledRun.HasIndex(x => new { x.TaskId, x.PlannedTicks }).IsUnique();
        scheduledRun.HasIndex(x => new { x.Status, x.RetryAtTicks });
        var plugin = modelBuilder.Entity<PluginState>();
        plugin.ToTable("PluginStates");
        plugin.HasKey(x => x.Id);
        var client = modelBuilder.Entity<McpClientApplication>();
        client.ToTable("McpClientApplications");
        client.HasKey(item => item.Id);
        client.Property(item => item.Name).HasMaxLength(100).IsRequired();
        client.Property(item => item.Description).HasMaxLength(500);
        client.Property(item => item.KeyId).HasMaxLength(32).IsRequired();
        client.Property(item => item.KeyHash).IsRequired();
        client.HasIndex(item => item.Name).IsUnique();
        client.HasIndex(item => item.KeyId).IsUnique();

        var toolSetting = modelBuilder.Entity<McpToolSetting>();
        toolSetting.ToTable("McpToolSettings");
        toolSetting.HasKey(item => item.ToolName);
        toolSetting.Property(item => item.ToolName).HasMaxLength(200);
        toolSetting.Property(item => item.UpdatedAtUtc)
            .HasConversion(
                value => value.UtcDateTime.Ticks,
                value => new DateTimeOffset(value, TimeSpan.Zero));

        var invocation = modelBuilder.Entity<McpInvocationLog>();
        invocation.ToTable("McpInvocationLogs");
        invocation.HasKey(item => item.Id);
        invocation.Property(item => item.ToolName).HasMaxLength(200);
        invocation.Property(item => item.RequestMethod).HasMaxLength(100).IsRequired().HasDefaultValue("tools/call");
        invocation.Property(item => item.Source).HasMaxLength(32).IsRequired().HasDefaultValue(InvocationSources.McpClient);
        invocation.Property(item => item.TargetName).HasMaxLength(200);
        invocation.Property(item => item.ErrorCode).HasMaxLength(100);
        invocation.Property(item => item.ErrorMessage).HasMaxLength(512);
        invocation.Property(item => item.TraceId).HasMaxLength(64);
        invocation.Property(item => item.OwnerId).HasMaxLength(64);
        invocation.Property(item => item.OwnerKind).HasMaxLength(16);
        invocation.HasIndex(item => new { item.OwnerKind, item.OwnerId, item.StartedAtUtc });
        invocation.Property(item => item.CorrelationId).HasMaxLength(64);
        invocation.Property(item => item.StartedAtUtc)
            .HasConversion(
                value => value.UtcDateTime.Ticks,
                value => new DateTimeOffset(value, TimeSpan.Zero));
        invocation.HasIndex(item => item.StartedAtUtc);
        invocation.HasIndex(item => new { item.ClientApplicationId, item.StartedAtUtc });
        invocation.HasIndex(item => new { item.ToolName, item.StartedAtUtc });
        invocation.HasIndex(item => new { item.RequestMethod, item.StartedAtUtc });
        invocation.HasIndex(item => new { item.Source, item.StartedAtUtc });
        invocation.HasOne(item => item.ClientApplication)
            .WithMany(item => item.InvocationLogs)
            .HasForeignKey(item => item.ClientApplicationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
