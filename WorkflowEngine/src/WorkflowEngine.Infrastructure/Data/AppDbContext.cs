using Microsoft.EntityFrameworkCore;
using WorkflowEngine.Infrastructure.Entities;

namespace WorkflowEngine.Infrastructure.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowDefinitionEntity> WorkflowDefinitions => Set<WorkflowDefinitionEntity>();

    public DbSet<WorkflowInstanceEntity> WorkflowInstances => Set<WorkflowInstanceEntity>();

    public DbSet<InstanceVariableEntity> InstanceVariables => Set<InstanceVariableEntity>();

    public DbSet<InstanceHistoryEntity> InstanceHistory => Set<InstanceHistoryEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkflowDefinitionEntity>(entity =>
        {
            entity.ToTable("workflow_definitions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Definition).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.Name, e.Version }).IsUnique();
            entity.HasIndex(e => e.Definition).HasMethod("gin");
        });

        modelBuilder.Entity<WorkflowInstanceEntity>(entity =>
        {
            entity.ToTable("workflow_instances");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ClaimedBy).HasMaxLength(300);
            entity.Property(e => e.StartedBy).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.CurrentStepId);
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
            entity.Property(e => e.SetAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.VariableName });
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
    }
}
