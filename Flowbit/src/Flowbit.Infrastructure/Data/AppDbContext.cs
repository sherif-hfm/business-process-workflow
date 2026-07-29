using Microsoft.EntityFrameworkCore;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Infrastructure.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<WorkflowDefinitionEntity> WorkflowDefinitions => Set<WorkflowDefinitionEntity>();

    public DbSet<WorkflowInstanceEntity> WorkflowInstances => Set<WorkflowInstanceEntity>();

    public DbSet<WorkflowBusinessKeyScopeEntity> WorkflowBusinessKeyScopes => Set<WorkflowBusinessKeyScopeEntity>();

    public DbSet<WorkflowBusinessKeyClaimEntity> WorkflowBusinessKeyClaims => Set<WorkflowBusinessKeyClaimEntity>();

    public DbSet<WorkflowIdempotencyClaimEntity> WorkflowIdempotencyClaims => Set<WorkflowIdempotencyClaimEntity>();

    public DbSet<MessageDeliveryReceiptEntity> MessageDeliveryReceipts => Set<MessageDeliveryReceiptEntity>();

    public DbSet<InstanceVariableEntity> InstanceVariables => Set<InstanceVariableEntity>();

    public DbSet<InstanceHistoryEntity> InstanceHistory => Set<InstanceHistoryEntity>();

    public DbSet<ExecutionTokenEntity> ExecutionTokens => Set<ExecutionTokenEntity>();

    public DbSet<NodeExecutionEntity> NodeExecutions => Set<NodeExecutionEntity>();

    public DbSet<UserTaskEntity> UserTasks => Set<UserTaskEntity>();
    public DbSet<MultiInstanceExecutionEntity> MultiInstanceExecutions => Set<MultiInstanceExecutionEntity>();
    public DbSet<MultiInstanceFlowCountEntity> MultiInstanceFlowCounts => Set<MultiInstanceFlowCountEntity>();
    public DbSet<GatewayExecutionEntity> GatewayExecutions => Set<GatewayExecutionEntity>();
    public DbSet<GatewayBranchEntity> GatewayBranches => Set<GatewayBranchEntity>();
    public DbSet<ComplexGatewayStateEntity> ComplexGatewayStates => Set<ComplexGatewayStateEntity>();

    public DbSet<SequenceFlowOccurrenceEntity> SequenceFlowOccurrences => Set<SequenceFlowOccurrenceEntity>();

    public DbSet<SequenceFlowSummaryEntity> SequenceFlowSummaries => Set<SequenceFlowSummaryEntity>();

    public DbSet<WorkflowSettingEntity> WorkflowSettings => Set<WorkflowSettingEntity>();

    public DbSet<EngineSettingEntity> EngineSettings => Set<EngineSettingEntity>();

    public DbSet<UserDelegationEntity> UserDelegations => Set<UserDelegationEntity>();

    public DbSet<WorkflowDelegationPolicyEntity> WorkflowDelegationPolicies =>
        Set<WorkflowDelegationPolicyEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(FlowbitDatabase.Schema);
        modelBuilder.HasPostgresExtension("citext");

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
            entity.HasIndex(e => new { e.WorkflowKey, e.Version }).IsUnique();
            // Supports the cross-version workflowKey instance search.
            entity.HasIndex(e => e.WorkflowKey);
            entity.HasIndex(e => e.Definition).HasMethod("gin");
        });

        modelBuilder.Entity<WorkflowInstanceEntity>(entity =>
        {
            entity.ToTable("workflow_instances");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.WorkflowKey).HasMaxLength(300).IsRequired();
            entity.Property(e => e.IdempotencyKey).HasMaxLength(300).UseCollation("C");
            entity.Property(e => e.BusinessKey).HasMaxLength(300).UseCollation("C");
            entity.Property(e => e.BusinessKeyUniqueness).HasMaxLength(32);
            entity.Property(e => e.StartedBy).HasMaxLength(300);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            // Supports the paged instance list ordered by UpdatedAt.
            entity.HasIndex(e => new { e.Status, e.UpdatedAt, e.Id });
            entity.HasIndex(e => new { e.UpdatedAt, e.Id });
            entity.HasIndex(e => new { e.CreatedAt, e.Id });
            entity.HasIndex(e => new { e.WorkflowKey, e.IdempotencyKey });
            entity.HasIndex(e => new { e.WorkflowKey, e.BusinessKey, e.Status });
            entity.HasOne(e => e.WorkflowDefinition)
                .WithMany(e => e.Instances)
                .HasForeignKey(e => e.WorkflowDefinitionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowBusinessKeyClaimEntity>()
                .WithMany()
                .HasForeignKey(e => new { e.WorkflowKey, e.BusinessKey })
                .HasPrincipalKey(e => new { e.WorkflowKey, e.BusinessKey })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowIdempotencyClaimEntity>()
                .WithMany()
                .HasForeignKey(e => new { e.WorkflowKey, e.IdempotencyKey })
                .HasPrincipalKey(e => new { e.WorkflowKey, e.IdempotencyKey })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExecutionTokenEntity>(entity =>
        {
            entity.ToTable("execution_tokens", table =>
                table.HasCheckConstraint(
                    "CK_execution_tokens_complex_gateway_registration",
                    "(\"ComplexGatewayStateId\" IS NULL AND \"ComplexGatewayCycle\" IS NULL) OR "
                    + "(\"ComplexGatewayStateId\" IS NOT NULL AND \"ComplexGatewayCycle\" IS NOT NULL "
                    + "AND \"ComplexGatewayCycle\" >= 0)"));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.NodeExternalId).HasMaxLength(300);
            entity.Property(e => e.NodeType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.FaultCode).HasMaxLength(ErrorEndConstraints.MaxCodeLength);
            entity.Property(e => e.FaultDescription).HasMaxLength(ErrorEndConstraints.MaxDescriptionLength);
            entity.Property(e => e.TerminationReason).HasMaxLength(64);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ComplexDrainStateIds)
                .HasColumnType("bigint[]")
                .IsRequired()
                .HasDefaultValueSql("'{}'::bigint[]");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.Status });
            entity.HasIndex(e => new { e.InstanceId, e.Id }).IsDescending(false, true);
            entity.HasIndex(e => new { e.InstanceId, e.NodeId, e.Status, e.ArrivedViaFlowId, e.Id });
            entity.HasIndex(e => new { e.GatewayBranchId, e.Status })
                .HasFilter("\"GatewayBranchId\" IS NOT NULL AND \"Status\" = 'active'");
            entity.HasIndex(e => new
                {
                    e.ComplexGatewayStateId,
                    e.ComplexGatewayCycle,
                    e.Status,
                    e.ArrivedViaFlowId,
                    e.Id
                })
                .HasFilter("\"ComplexGatewayStateId\" IS NOT NULL AND \"Status\" = 'active'");
            entity.HasIndex(e => new { e.NodeId, e.Status });
            entity.HasIndex(e => new { e.NodeExternalId, e.Status });
            entity.HasIndex(e => e.ComplexDrainStateIds)
                .HasMethod("gin")
                .HasFilter("\"Status\" = 'active' AND cardinality(\"ComplexDrainStateIds\") > 0");
            entity.HasIndex(e => e.CurrentNodeExecutionId).IsUnique();
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.Tokens)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.GatewayBranch)
                .WithMany(e => e.Tokens)
                .HasForeignKey(e => e.GatewayBranchId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.ComplexGatewayState)
                .WithMany(e => e.WaitingTokens)
                .HasForeignKey(e => e.ComplexGatewayStateId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.CurrentNodeExecution)
                .WithOne()
                .HasForeignKey<ExecutionTokenEntity>(e => e.CurrentNodeExecutionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<NodeExecutionEntity>(entity =>
        {
            entity.ToTable("node_executions", table =>
            {
                table.HasCheckConstraint(
                    "CK_node_executions_execution_kind",
                    "\"ExecutionKind\" IN ('node', 'userTaskItem')");
                table.HasCheckConstraint(
                    "CK_node_executions_status",
                    "\"Status\" IN ('pending', 'active', 'completed', 'cancelled', 'faulted', 'merged')");
                table.HasCheckConstraint(
                    "CK_node_executions_completion_reason",
                    "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR "
                    + "(\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') "
                    + "AND \"CompletionReason\" IN "
                    + "('normal', 'userAction', 'messageDelivery', 'multiInstanceItem', "
                    + "'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', "
                    + "'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', "
                    + "'gatewayScopeCancelled', 'gatewayJoinMerged', 'parallelFork', "
                    + "'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', "
                    + "'complexActivation', 'complexReset', 'scopedInterrupt', "
                    + "'scopedInterruptSkipped')))");
                table.HasCheckConstraint(
                    "CK_node_executions_multi_instance_shape",
                    "(\"ExecutionKind\" = 'node' AND \"MultiInstanceExecutionId\" IS NULL AND \"ItemIndex\" IS NULL) OR "
                    + "(\"ExecutionKind\" = 'userTaskItem' AND \"UserTaskId\" IS NOT NULL "
                    + "AND \"MultiInstanceExecutionId\" IS NOT NULL AND \"ItemIndex\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_node_executions_timestamps",
                    "(\"Status\" = 'pending' AND \"StartedAt\" IS NULL AND \"CompletedAt\" IS NULL) OR "
                    + "(\"Status\" = 'active' AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NULL) OR "
                    + "(\"Status\" = 'cancelled' AND \"CompletedAt\" IS NOT NULL) OR "
                    + "(\"Status\" IN ('completed', 'faulted', 'merged') "
                    + "AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)");
                table.HasCheckConstraint(
                    "CK_node_executions_timestamp_order",
                    "(\"StartedAt\" IS NULL OR \"StartedAt\" >= \"CreatedAt\") "
                    + "AND \"UpdatedAt\" >= \"CreatedAt\" "
                    + "AND (\"CompletedAt\" IS NULL OR \"CompletedAt\" >= COALESCE(\"StartedAt\", \"CreatedAt\"))");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.NodeExternalId).HasMaxLength(300);
            entity.Property(e => e.NodeType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ExecutionKind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CompletionReason).HasMaxLength(64);
            entity.Property(e => e.NodeRolesJson).HasColumnType("jsonb");
            entity.Property(e => e.TriggeredBy).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.TriggeredByRolesJson).HasColumnType("jsonb");
            entity.Property(e => e.TriggeredActingFor).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.CompletedBy).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.CompletedByRolesJson).HasColumnType("jsonb");
            entity.Property(e => e.CompletedActingFor).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.ErrorCode).HasMaxLength(ErrorEndConstraints.MaxCodeLength);
            entity.Property(e => e.ErrorDescription).HasMaxLength(ErrorEndConstraints.MaxDescriptionLength);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.IsCutoverSeeded).HasDefaultValue(false);
            entity.HasIndex(e => new { e.UpdatedAt, e.Id });
            entity.HasIndex(e => new { e.Status, e.UpdatedAt, e.Id });
            entity.HasIndex(e => new { e.InstanceId, e.UpdatedAt, e.Id });
            entity.HasIndex(e => new { e.CreatedAt, e.Id });
            entity.HasIndex(e => new { e.StartedAt, e.Id });
            entity.HasIndex(e => new { e.CompletedAt, e.Id });
            entity.HasIndex(e => new { e.ExecutionTokenId, e.Status });
            entity.HasIndex(e => new { e.NodeId, e.Status, e.StartedAt, e.Id });
            entity.HasIndex(e => new { e.NodeExternalId, e.Status, e.StartedAt, e.Id });
            entity.HasIndex(e => new { e.NodeType, e.Status, e.StartedAt, e.Id });
            entity.HasIndex(e => e.UserTaskId)
                .IsUnique()
                .HasFilter("\"UserTaskId\" IS NOT NULL");
            entity.HasIndex(e => new { e.MultiInstanceExecutionId, e.ItemIndex })
                .IsUnique()
                .HasFilter("\"MultiInstanceExecutionId\" IS NOT NULL AND \"ItemIndex\" IS NOT NULL");
            entity.HasIndex(e => e.EntryGatewayBranchId);
            entity.HasIndex(e => e.ExitGatewayBranchId);
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.NodeExecutions)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.ExecutionToken)
                .WithMany(e => e.NodeExecutions)
                .HasForeignKey(e => e.ExecutionTokenId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.UserTask)
                .WithOne(e => e.NodeExecution)
                .HasForeignKey<NodeExecutionEntity>(e => e.UserTaskId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.MultiInstanceExecution)
                .WithMany(e => e.NodeExecutions)
                .HasForeignKey(e => e.MultiInstanceExecutionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.EntryGatewayBranch)
                .WithMany(e => e.EnteredNodeExecutions)
                .HasForeignKey(e => e.EntryGatewayBranchId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.ExitGatewayBranch)
                .WithMany(e => e.ExitedNodeExecutions)
                .HasForeignKey(e => e.ExitGatewayBranchId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<UserTaskEntity>(entity =>
        {
            entity.ToTable("user_tasks");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.NodeName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.NodeExternalId).HasMaxLength(300);
            entity.Property(e => e.Roles).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.RequiresAssignment).HasDefaultValue(false);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ClaimedBy).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.Assignee).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.CompletedBy).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.CompletedByRoles).HasColumnType("text[]");
            entity.Property(e => e.CompletedActingFor).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.ItemValueJson).HasColumnType("jsonb");
            entity.Property(e => e.ResultJson).HasColumnType("jsonb");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.Status, e.UpdatedAt, e.Id });
            entity.HasIndex(e => new { e.Status, e.CreatedAt, e.Id });
            entity.HasIndex(e => new { e.InstanceId, e.Status });
            entity.HasIndex(e => new { e.InstanceId, e.Status, e.CompletedAt, e.Id });
            entity.HasIndex(e => new { e.NodeId, e.Status });
            entity.HasIndex(e => new { e.NodeExternalId, e.Status });
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

        modelBuilder.Entity<WorkflowBusinessKeyScopeEntity>(entity =>
        {
            entity.ToTable("workflow_business_key_scopes");
            entity.HasKey(e => e.WorkflowKey);
            entity.Property(e => e.WorkflowKey).HasMaxLength(300);
            entity.Property(e => e.ActivatedAt).HasDefaultValueSql("now()");
        });

        modelBuilder.Entity<WorkflowBusinessKeyClaimEntity>(entity =>
        {
            entity.ToTable("workflow_business_key_claims");
            entity.HasKey(e => new { e.WorkflowKey, e.BusinessKey });
            entity.Property(e => e.WorkflowKey).HasMaxLength(300);
            entity.Property(e => e.BusinessKey).HasMaxLength(300).UseCollation("C");
            entity.HasIndex(e => e.ActiveInstanceId);
            entity.HasIndex(e => e.LastInstanceId);
            entity.HasOne<WorkflowInstanceEntity>()
                .WithMany()
                .HasForeignKey(e => e.ActiveInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkflowInstanceEntity>()
                .WithMany()
                .HasForeignKey(e => e.LastInstanceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowIdempotencyClaimEntity>(entity =>
        {
            entity.ToTable("workflow_idempotency_claims");
            entity.HasKey(e => new { e.WorkflowKey, e.IdempotencyKey });
            entity.Property(e => e.WorkflowKey).HasMaxLength(300);
            entity.Property(e => e.IdempotencyKey).HasMaxLength(300).UseCollation("C");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => e.InstanceId).IsUnique();
            entity.HasOne<WorkflowInstanceEntity>()
                .WithMany()
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MessageDeliveryReceiptEntity>(entity =>
        {
            entity.ToTable("message_delivery_receipts");
            entity.HasKey(e => new { e.InstanceId, e.IdempotencyKey });
            entity.Property(e => e.IdempotencyKey).HasMaxLength(300).UseCollation("C");
            entity.Property(e => e.CorrelationHeaderName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.CredentialProofSalt).IsRequired();
            entity.Property(e => e.CredentialProofHash).IsRequired();
            entity.Property(e => e.EnvelopeProofSalt).IsRequired();
            entity.Property(e => e.EnvelopeProofHash).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => e.WaitHistoryId).IsUnique();
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.MessageDeliveryReceipts)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.WaitHistory)
                .WithMany()
                .HasForeignKey(e => e.WaitHistoryId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MultiInstanceExecutionEntity>(entity =>
        {
            entity.ToTable("multi_instance_executions");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Mode).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Source).HasMaxLength(32).IsRequired();
            entity.Property(e => e.OnePerActor).HasDefaultValue(false);
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

        modelBuilder.Entity<GatewayExecutionEntity>(entity =>
        {
            entity.ToTable("gateway_executions", table =>
            {
                table.HasCheckConstraint(
                    "CK_gateway_executions_gateway_type",
                    "\"GatewayType\" IN ('parallelGateway', 'inclusiveGateway', 'complexGateway')");
                table.HasCheckConstraint(
                    "CK_gateway_executions_direction",
                    "\"Direction\" IN ('split', 'merge')");
                table.HasCheckConstraint(
                    "CK_gateway_executions_status",
                    "\"Status\" IN ('active', 'joined', 'completed', 'interrupted', 'cancelled')");
                table.HasCheckConstraint(
                    "CK_gateway_executions_phase_cycle",
                    "(\"GatewayType\" = 'complexGateway' "
                    + "AND \"Phase\" IN ('start', 'reset') AND \"Cycle\" IS NOT NULL AND \"Cycle\" >= 0) OR "
                    + "(\"GatewayType\" <> 'complexGateway' AND \"Phase\" IS NULL AND \"Cycle\" IS NULL)");
                table.HasCheckConstraint(
                    "CK_gateway_executions_selected_flows",
                    "cardinality(\"SelectedFlowIds\") >= 1 OR "
                    + "(\"GatewayType\" = 'complexGateway' AND \"Phase\" = 'reset')");
                table.HasCheckConstraint(
                    "CK_gateway_executions_completed_at",
                    "(\"Status\" = 'active' AND \"CompletedAt\" IS NULL) OR "
                    + "(\"Status\" <> 'active' AND \"CompletedAt\" IS NOT NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.GatewayType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Direction).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Phase).HasMaxLength(32);
            entity.Property(e => e.SelectedFlowIds)
                .HasColumnType("integer[]")
                .IsRequired()
                .HasDefaultValueSql("'{}'::integer[]");
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CompletionReason).HasMaxLength(64);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.Status })
                .HasFilter("\"Status\" = 'active'");
            entity.HasIndex(e => new { e.InstanceId, e.GatewayNodeId, e.Status })
                .HasFilter("\"Status\" = 'active'");
            entity.HasIndex(e => new { e.ParentBranchId, e.Status })
                .HasFilter("\"Status\" = 'active'");
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.GatewayExecutions)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.ParentBranch)
                .WithMany(e => e.ChildExecutions)
                .HasForeignKey(e => e.ParentBranchId)
                .OnDelete(DeleteBehavior.NoAction);
            entity.HasOne(e => e.InterruptingToken)
                .WithMany(e => e.InterruptedGatewayExecutions)
                .HasForeignKey(e => e.InterruptingTokenId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<GatewayBranchEntity>(entity =>
        {
            entity.ToTable("gateway_branches", table =>
            {
                table.HasCheckConstraint(
                    "CK_gateway_branches_status",
                    "\"Status\" IN ('active', 'merged', 'completed', 'interrupted', 'cancelled')");
                table.HasCheckConstraint(
                    "CK_gateway_branches_ordinal",
                    "\"Ordinal\" >= 0");
                table.HasCheckConstraint(
                    "CK_gateway_branches_completed_at",
                    "(\"Status\" = 'active' AND \"CompletedAt\" IS NULL) OR "
                    + "(\"Status\" <> 'active' AND \"CompletedAt\" IS NOT NULL)");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Status).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.ExecutionId, e.Status })
                .HasFilter("\"Status\" = 'active'");
            entity.HasIndex(e => new { e.ExecutionId, e.OriginatingFlowId }).IsUnique();
            entity.HasIndex(e => new { e.ExecutionId, e.Ordinal }).IsUnique();
            entity.HasOne(e => e.Execution)
                .WithMany(e => e.Branches)
                .HasForeignKey(e => e.ExecutionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ComplexGatewayStateEntity>(entity =>
        {
            entity.ToTable("complex_gateway_states", table =>
            {
                table.HasCheckConstraint(
                    "CK_complex_gateway_states_phase",
                    "\"Phase\" IN ('waitingForStart', 'waitingForReset', 'interruptedDraining')");
                table.HasCheckConstraint(
                    "CK_complex_gateway_states_cycle",
                    "\"Cycle\" >= 0");
                table.HasCheckConstraint(
                    "CK_complex_gateway_states_draining_tokens",
                    "\"Phase\" = 'interruptedDraining' OR cardinality(\"DrainingTokenIds\") = 0");
                table.HasCheckConstraint(
                    "CK_complex_gateway_states_activation_drain_states",
                    "\"Phase\" <> 'waitingForStart' OR cardinality(\"ActivationDrainStateIds\") = 0");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Phase).HasMaxLength(32).IsRequired();
            entity.Property(e => e.ContributingFlowIds)
                .HasColumnType("integer[]")
                .IsRequired()
                .HasDefaultValueSql("'{}'::integer[]");
            entity.Property(e => e.RemainingFlowIds)
                .HasColumnType("integer[]")
                .IsRequired()
                .HasDefaultValueSql("'{}'::integer[]");
            entity.Property(e => e.DrainingTokenIds)
                .HasColumnType("bigint[]")
                .IsRequired()
                .HasDefaultValueSql("'{}'::bigint[]");
            entity.Property(e => e.ActivationDrainStateIds)
                .HasColumnType("bigint[]")
                .IsRequired()
                .HasDefaultValueSql("'{}'::bigint[]");
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.GatewayNodeId }).IsUnique();
            entity.HasIndex(e => new { e.InstanceId, e.Phase })
                .HasFilter("\"Phase\" <> 'waitingForStart'");
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.ComplexGatewayStates)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(e => e.ActiveExecution)
                .WithMany(e => e.ActiveComplexStates)
                .HasForeignKey(e => e.ActiveExecutionId)
                .OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<SequenceFlowOccurrenceEntity>(entity =>
        {
            entity.ToTable("sequence_flow_occurrences", table =>
                table.HasCheckConstraint(
                    "CK_sequence_flow_occurrences_action_or_traversal",
                    "\"IsAction\" OR \"IsTraversal\""));
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Kind).HasMaxLength(32).IsRequired();
            entity.Property(e => e.User).HasMaxLength(300);
            entity.Property(e => e.UserRoles).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.ActingFor).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.ValuesJson).HasColumnType("jsonb");
            entity.Property(e => e.OccurredAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.SequenceFlowId, e.Id })
                .IsDescending(false, false, true);
            entity.HasIndex(e => e.UserTaskId)
                .IsUnique()
                .HasFilter("\"UserTaskId\" IS NOT NULL AND \"IsAction\"");
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.SequenceFlowOccurrences)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SequenceFlowSummaryEntity>(entity =>
        {
            entity.ToTable("sequence_flow_summaries");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.LastActionUser).HasMaxLength(300);
            entity.Property(e => e.LastActionUserRoles).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.LastActionActingFor).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.LastActionKind).HasMaxLength(32);
            entity.Property(e => e.LastActionValuesJson).HasColumnType("jsonb");
            entity.Property(e => e.LastTraversalUser).HasMaxLength(300);
            entity.Property(e => e.LastTraversalUserRoles).HasColumnType("text[]").IsRequired().HasDefaultValueSql("'{}'::text[]");
            entity.Property(e => e.LastTraversalActingFor).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.LastTraversalKind).HasMaxLength(32);
            entity.Property(e => e.LastTraversalValuesJson).HasColumnType("jsonb");
            entity.HasIndex(e => new { e.InstanceId, e.SequenceFlowId }).IsUnique();
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.SequenceFlowSummaries)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InstanceVariableEntity>(entity =>
        {
            entity.ToTable("instance_variables");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.VariableName).HasMaxLength(300).IsRequired();
            entity.Property(e => e.ValueJson).HasColumnType("jsonb");
            entity.Property(e => e.SetBy).HasMaxLength(300);
            entity.Property(e => e.ActingFor).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.SetAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.InstanceId, e.VariableName, e.Id })
                .IsDescending(false, false, true);
            // Leads with VariableName to support value lookups in the variable search.
            entity.HasIndex(e => new { e.VariableName, e.InstanceId });
            entity.HasOne(e => e.Instance)
                .WithMany(e => e.Variables)
                .HasForeignKey(e => e.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.NodeExecutionId);
            entity.HasOne(e => e.NodeExecution)
                .WithMany()
                .HasForeignKey(e => e.NodeExecutionId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<InstanceHistoryEntity>(entity =>
        {
            entity.ToTable("instance_history");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Payload).HasColumnType("jsonb");
            entity.Property(e => e.PerformedBy).HasMaxLength(300);
            entity.Property(e => e.ActingFor).HasMaxLength(UserTaskConstraints.MaxActorNameLength);
            entity.Property(e => e.Note).HasMaxLength(1000);
            entity.Property(e => e.PerformedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => e.InstanceId);
            entity.HasIndex(e => new { e.InstanceId, e.TokenId, e.ToStepId, e.Id })
                .IsDescending(false, false, false, true);
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

        modelBuilder.Entity<UserDelegationEntity>(entity =>
        {
            entity.ToTable("user_delegations", table =>
            {
                table.HasCheckConstraint(
                    "CK_user_delegations_validity",
                    "\"ValidUntil\" > \"ValidFrom\"");
                table.HasCheckConstraint(
                    "CK_user_delegations_acceptance_state",
                    "\"AcceptanceState\" IN ('notRequired', 'pending', 'accepted', 'rejected')");
                table.HasCheckConstraint(
                    "CK_user_delegations_acceptance_shape",
                    "((NOT \"RequiresAcceptance\" AND \"AcceptanceState\" = 'notRequired' "
                    + "AND \"DecisionBy\" IS NULL AND \"DecisionAt\" IS NULL AND \"DecisionReason\" IS NULL) "
                    + "OR (\"RequiresAcceptance\" AND \"AcceptanceState\" = 'pending' "
                    + "AND \"DecisionBy\" IS NULL AND \"DecisionAt\" IS NULL AND \"DecisionReason\" IS NULL) "
                    + "OR (\"RequiresAcceptance\" AND \"AcceptanceState\" IN ('accepted', 'rejected') "
                    + "AND \"DecisionBy\" IS NOT NULL AND \"DecisionAt\" IS NOT NULL))");
                table.HasCheckConstraint(
                    "CK_user_delegations_revocation_shape",
                    "((\"RevokedAt\" IS NULL AND \"RevokedBy\" IS NULL AND \"RevocationReason\" IS NULL) "
                    + "OR (\"RevokedAt\" IS NOT NULL AND \"RevokedBy\" IS NOT NULL))");
                table.HasCheckConstraint(
                    "CK_user_delegations_timestamps",
                    "\"UpdatedAt\" >= \"CreatedAt\" "
                    + "AND (\"DecisionAt\" IS NULL OR \"DecisionAt\" >= \"CreatedAt\") "
                    + "AND (\"RevokedAt\" IS NULL OR \"RevokedAt\" >= \"CreatedAt\")");
            });
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Delegator)
                .HasColumnType("citext")
                .HasMaxLength(UserDelegationConstraints.MaxActorNameLength)
                .IsRequired();
            entity.Property(e => e.Delegate)
                .HasColumnType("citext")
                .HasMaxLength(UserDelegationConstraints.MaxActorNameLength)
                .IsRequired();
            entity.Property(e => e.WorkflowKey)
                .HasMaxLength(UserDelegationConstraints.MaxWorkflowKeyLength)
                .IsRequired();
            entity.Property(e => e.AcceptanceState).HasMaxLength(32).IsRequired();
            entity.Property(e => e.CreatedBy)
                .HasMaxLength(UserDelegationConstraints.MaxActorNameLength)
                .IsRequired();
            entity.Property(e => e.CreationReason)
                .HasMaxLength(UserDelegationConstraints.MaxReasonLength);
            entity.Property(e => e.DecisionBy)
                .HasMaxLength(UserDelegationConstraints.MaxActorNameLength);
            entity.Property(e => e.DecisionReason)
                .HasMaxLength(UserDelegationConstraints.MaxReasonLength);
            entity.Property(e => e.RevokedBy)
                .HasMaxLength(UserDelegationConstraints.MaxActorNameLength);
            entity.Property(e => e.RevocationReason)
                .HasMaxLength(UserDelegationConstraints.MaxReasonLength);
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
            entity.HasIndex(e => new
            {
                e.Delegate,
                e.WorkflowKey,
                e.AcceptanceState,
                e.RevokedAt,
                e.ValidFrom,
                e.ValidUntil
            });
            entity.HasIndex(e => new
            {
                e.Delegator,
                e.WorkflowKey,
                e.CreatedAt,
                e.Id
            });
            entity.HasIndex(e => new
            {
                e.Delegator,
                e.Delegate,
                e.WorkflowKey,
                e.AcceptanceState,
                e.RevokedAt,
                e.ValidFrom,
                e.ValidUntil
            });
            entity.HasIndex(e => new { e.WorkflowKey, e.ValidUntil });
        });

        modelBuilder.Entity<WorkflowDelegationPolicyEntity>(entity =>
        {
            entity.ToTable("workflow_delegation_policies");
            entity.HasKey(e => e.WorkflowKey);
            entity.Property(e => e.WorkflowKey)
                .HasMaxLength(UserDelegationConstraints.MaxWorkflowKeyLength);
            entity.Property(e => e.CreatedBy)
                .HasMaxLength(UserDelegationConstraints.MaxActorNameLength)
                .IsRequired();
            entity.Property(e => e.UpdatedBy)
                .HasMaxLength(UserDelegationConstraints.MaxActorNameLength)
                .IsRequired();
            entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
        });
    }
}
