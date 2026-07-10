using Microsoft.EntityFrameworkCore;
using WorkflowEngine.Infrastructure.Entities;

namespace WorkflowEngine.Infrastructure.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowDefinitionEntity> WorkflowDefinitions => Set<WorkflowDefinitionEntity>();

    public DbSet<WorkflowInstanceEntity> WorkflowInstances => Set<WorkflowInstanceEntity>();

    public DbSet<InstanceVariableEntity> InstanceVariables => Set<InstanceVariableEntity>();

    public DbSet<InstanceHistoryEntity> InstanceHistory => Set<InstanceHistoryEntity>();

    public DbSet<WorkflowSettingEntity> WorkflowSettings => Set<WorkflowSettingEntity>();

    public DbSet<EngineSettingEntity> EngineSettings => Set<EngineSettingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowDefinitionEntity>(entity =>
        {
            entity.ToTable("workflow_definitions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.Property(e => e.WorkflowKey).HasMaxLength(300).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(e => e.Definition).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.Name, e.Version }).IsUnique();
            // Supports the cross-version workflowKey instance search.
            entity.HasIndex(e => e.WorkflowKey);
            entity.HasIndex(e => e.Definition).HasMethod("gin");
        });

        modelBuilder.Entity<WorkflowInstanceEntity>(entity =>
        {
            entity.ToTable("workflow_instances");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CurrentNodeName).HasMaxLength(300).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(e => e.CurrentNodeExternalId).HasMaxLength(300);
            entity.Property(e => e.CurrentNodeType).HasMaxLength(32).IsRequired().HasDefaultValue(string.Empty);
            entity.Property(e => e.CurrentNodeRoles).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.CurrentRequiresClaim).IsRequired().HasDefaultValue(false);
            entity.Property(e => e.ClaimedBy).HasMaxLength(300);
            entity.Property(e => e.StartedBy).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => e.CurrentStepId);
            // Supports the inbox filter (running user tasks) and its UpdatedAt ordering.
            entity.HasIndex(e => new { e.Status, e.CurrentNodeType, e.UpdatedAt });
            // Supports the paged instance list ordered by UpdatedAt.
            entity.HasIndex(e => new { e.Status, e.UpdatedAt, e.Id });
            // Supports array-overlap role matching in the inbox filter.
            entity.HasIndex(e => e.CurrentNodeRoles).HasMethod("gin");
            // Supports the current-node externalId filter on the list/inbox reads.
            entity.HasIndex(e => new { e.Status, e.CurrentNodeExternalId });
            entity.HasOne(e => e.WorkflowDefinition)
                .WithMany(e => e.Instances)
                .HasForeignKey(e => e.WorkflowDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstanceVariableEntity>(entity =>
        {
            entity.ToTable("instance_variables");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.VariableName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.ValueJson).HasColumnType("jsonb");
            entity.Property(e => e.SetBy).HasMaxLength(300);
            entity.Property(e => e.SetAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.VariableName });
            // Leads with VariableName to support value lookups in the variable search.
            entity.HasIndex(e => new { e.VariableName, e.InstanceId });
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.Variables)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InstanceHistoryEntity>(entity =>
        {
            entity.ToTable("instance_history");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Payload).HasColumnType("jsonb");
            entity.Property(e => e.PerformedBy).HasMaxLength(300);
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.Property(e => e.PerformedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => e.InstanceId);
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.History)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkflowSettingEntity>(entity =>
        {
            entity.ToTable("workflow_settings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Namespace).HasMaxLength(300);
            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Value).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.Namespace, e.Name }).IsUnique();
        });

        modelBuilder.Entity<EngineSettingEntity>(entity =>
        {
            entity.ToTable("engine_settings");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Namespace).HasMaxLength(300);
            entity.Property(e => e.Key).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Value).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.Namespace, e.Key }).IsUnique();
        });
    }
}
