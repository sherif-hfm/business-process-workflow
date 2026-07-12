using Microsoft.EntityFrameworkCore;
using WorkflowEngine.Infrastructure.Entities;

namespace WorkflowEngine.Infrastructure.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowDefinitionEntity> WorkflowDefinitions => Set<WorkflowDefinitionEntity>();

    public DbSet<WorkflowInstanceEntity> WorkflowInstances => Set<WorkflowInstanceEntity>();

    public DbSet<InstanceVariableEntity> InstanceVariables => Set<InstanceVariableEntity>();

    public DbSet<InstanceHistoryEntity> InstanceHistory => Set<InstanceHistoryEntity>();

    public DbSet<ExecutionTokenEntity> ExecutionTokens => Set<ExecutionTokenEntity>();

    public DbSet<UserTaskEntity> UserTasks => Set<UserTaskEntity>();
    public DbSet<MultiInstanceExecutionEntity> MultiInstanceExecutions => Set<MultiInstanceExecutionEntity>();
    public DbSet<MultiInstanceFlowCountEntity> MultiInstanceFlowCounts => Set<MultiInstanceFlowCountEntity>();

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
            entity.Property(e => e.IsPublished);
            entity.Property(e => e.IsDefault);
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
            entity.Property(e => e.StartedBy).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            // Supports the paged instance list ordered by UpdatedAt.
            entity.HasIndex(e => new { e.Status, e.UpdatedAt, e.Id });
            entity.HasOne(e => e.WorkflowDefinition)
                .WithMany(e => e.Instances)
                .HasForeignKey(e => e.WorkflowDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExecutionTokenEntity>(entity =>
        {
            entity.ToTable("execution_tokens");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.NodeExternalId).HasMaxLength(300);
            entity.Property(e => e.NodeType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.Status });
            entity.HasIndex(e => new { e.NodeId, e.Status });
            entity.HasIndex(e => new { e.NodeExternalId, e.Status });
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.Tokens)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserTaskEntity>(entity =>
        {
            entity.ToTable("user_tasks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.NodeExternalId).HasMaxLength(300);
            entity.Property(e => e.Roles).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ClaimedBy).HasMaxLength(300);
            entity.Property(e => e.Assignee).HasMaxLength(300);
            entity.Property(e => e.CompletedBy).HasMaxLength(300);
            entity.Property(e => e.ItemValueJson).HasColumnType("jsonb");
            entity.Property(e => e.ResultJson).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.Status, e.UpdatedAt, e.Id });
            entity.HasIndex(e => new { e.InstanceId, e.Status });
            entity.HasIndex(e => new { e.NodeId, e.Status });
            entity.HasIndex(e => new { e.NodeExternalId, e.Status });
            entity.HasIndex(e => new { e.Assignee, e.Status, e.UpdatedAt, e.Id });
            entity.HasIndex(e => new { e.MultiInstanceExecutionId, e.Status, e.ItemIndex });
            entity.HasIndex(e => new { e.MultiInstanceExecutionId, e.ItemIndex }).IsUnique();
            entity.HasIndex(e => e.Roles).HasMethod("gin");
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.UserTasks)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Token)
                .WithMany(e => e.UserTasks)
                .HasForeignKey(e => e.TokenId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.MultiInstanceExecution)
                .WithMany(e => e.UserTasks)
                .HasForeignKey(e => e.MultiInstanceExecutionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MultiInstanceExecutionEntity>(entity =>
        {
            entity.ToTable("multi_instance_executions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Mode).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ResultVariable).HasMaxLength(300).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CompletionReason).HasMaxLength(32);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.Status });
            entity.HasIndex(e => new { e.TokenId, e.NodeId, e.Status });
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.MultiInstanceExecutions)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.Token)
                .WithMany(e => e.MultiInstanceExecutions)
                .HasForeignKey(e => e.TokenId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MultiInstanceFlowCountEntity>(entity =>
        {
            entity.ToTable("multi_instance_flow_counts");
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ExecutionId, e.FlowId }).IsUnique();
            entity.HasOne(e => e.Execution)
                .WithMany(e => e.FlowCounts)
                .HasForeignKey(e => e.ExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
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
