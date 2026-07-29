using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Infrastructure.Repositories;

public sealed class WorkflowRuntimeRepository(AppDbContext dbContext) : IWorkflowRuntimeRepository
{
    private readonly HashSet<long> loadedSequenceFlowSummaryInstances = [];

    public async Task<WorkflowInstanceRecord> AddInstanceAsync(
        long workflowDefinitionId,
        string workflowKey,
        string? idempotencyKey,
        string? businessKey,
        string? businessKeyUniqueness,
        CurrentNodeSnapshot node,
        string? startedBy,
        IReadOnlyList<string> startedByRoles,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = workflowDefinitionId,
            WorkflowKey = workflowKey,
            IdempotencyKey = idempotencyKey,
            BusinessKey = businessKey,
            BusinessKeyUniqueness = businessKeyUniqueness,
            Status = WorkflowInstanceStatuses.Running,
            StartedBy = startedBy,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.WorkflowInstances.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        // Persist the entry token immediately. Parallel forks need a durable token
        // identity before the first pass-through hop so every branch and history
        // record can be correlated to the activation that created it.
        var token = NewToken(entity, node, now);
        dbContext.ExecutionTokens.Add(token);
        await dbContext.SaveChangesAsync(cancellationToken);
        var nodeExecution = NewNodeExecution(
            entity,
            token,
            node,
            NodeExecutionKinds.Node,
            NodeExecutionStatuses.Active,
            null,
            null,
            new NodeExecutionActorRecord(startedBy, startedByRoles),
            now);
        dbContext.NodeExecutions.Add(nodeExecution);
        token.CurrentNodeExecution = nodeExecution;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(entity, token, null);
    }

    // EF1002: the SQL is assembled from static fragments plus @paramName placeholders
    // only; every caller-supplied name/value is bound via NpgsqlParameter, so there is
    // no interpolation of untrusted input and no injection surface.
#pragma warning disable EF1002
    public async Task<PagedResult<InstanceListItem>> ListInstancesAsync(
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
        IReadOnlyList<InstanceSortCriterion> sort,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var where = new StringBuilder(" WHERE 1=1");
        var args = new List<(string Name, object Value)>();

        if (!string.IsNullOrWhiteSpace(status))
        {
            args.Add(("status", status));
            where.Append(" AND w.\"Status\" = @status");
        }

        AppendInstanceIdFilter(where, args, instanceId);
        AppendWorkflowIdFilter(where, args, workflowId);
        AppendWorkflowKeyFilter(where, args, workflowKey);
        AppendBusinessKeyFilter(where, args, businessKey);
        AppendNodeIdFilter(where, args, nodeId);
        AppendNodeExternalIdFilter(where, args, nodeExternalId);
        AppendVariableFilters(where, args, variableFilters);

        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS \"Value\" FROM flowbit.workflow_instances w{where}",
                BuildParameters(args))
            .SingleAsync(cancellationToken);

        var skip = (page - 1) * pageSize;
        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("take", pageSize),
            ("skip", skip)
        };
        var orderBy = BuildInstanceOrderBy(sort);
        var entities = await dbContext.WorkflowInstances
            .FromSqlRaw(
                $"SELECT w.* FROM flowbit.workflow_instances w{where} ORDER BY {orderBy} LIMIT @take OFFSET @skip",
                BuildParameters(pageArgs))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var items = await ToListItemsAsync(entities, includeVariables, cancellationToken);
        return new PagedResult<InstanceListItem>(items, page, pageSize, totalCount);
    }

    public async Task<PagedResult<InboxListItem>> ListInboxAsync(
        string user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset asOf,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
        IReadOnlyList<InboxSortCriterion> sort,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Roles are matched case-insensitively (mirrors the in-memory role check),
        // so compare lower-cased node roles against lower-cased actor roles.
        var (where, args) = BuildInboxWhere(
            user, roles, asOf, instanceId, workflowId, workflowKey, businessKey,
            nodeId, nodeExternalId, variableFilters);
        var eligibleOrderBy = BuildInboxOrderBy(sort, "e");
        var pageOrderBy = BuildInboxOrderBy(sort, "page");
        var eligibleCte = $"""
            WITH eligible AS (
                SELECT ut."Id",
                       ut."InstanceId",
                       ut."MultiInstanceExecutionId",
                       delegation."Id" AS "DelegationId",
                       delegation."Delegator" AS "ActingFor",
                       ut."CreatedAt" AS "TaskCreatedAt",
                       ut."UpdatedAt" AS "TaskUpdatedAt",
                       w."CreatedAt" AS "InstanceCreatedAt",
                       w."UpdatedAt" AS "InstanceUpdatedAt",
                       ROW_NUMBER() OVER (
                           PARTITION BY CASE
                               WHEN COALESCE(mie."OnePerActor", FALSE) THEN mie."Id"
                               ELSE -ut."Id"
                           END,
                           CASE
                               WHEN COALESCE(mie."OnePerActor", FALSE)
                               THEN lower(COALESCE(delegation."Delegator", @user))
                               ELSE ''
                           END
                           ORDER BY
                               CASE
                                   WHEN COALESCE(mie."OnePerActor", FALSE)
                                        AND lower(ut."Assignee") =
                                            lower(COALESCE(delegation."Delegator", @user)) THEN 0
                                   WHEN COALESCE(mie."OnePerActor", FALSE)
                                        AND lower(ut."ClaimedBy") =
                                            lower(COALESCE(delegation."Delegator", @user)) THEN 1
                                   ELSE 2
                               END,
                               ut."UpdatedAt" DESC,
                               ut."Id" DESC
                       ) AS inbox_rank
                FROM flowbit.user_tasks ut
                JOIN flowbit.workflow_instances w ON ut."InstanceId" = w."Id"
                JOIN flowbit.workflow_definitions wd ON w."WorkflowDefinitionId" = wd."Id"
                LEFT JOIN flowbit.multi_instance_executions mie ON mie."Id" = ut."MultiInstanceExecutionId"
                LEFT JOIN LATERAL (
                    SELECT d."Id", d."Delegator"
                    FROM flowbit.user_delegations d
                    WHERE d."Delegate" = @user
                      AND d."Delegator" = COALESCE(ut."Assignee", ut."ClaimedBy")
                      AND d."WorkflowKey" = w."WorkflowKey"
                      AND d."RevokedAt" IS NULL
                      AND d."ValidFrom" <= @delegationAsOf
                      AND @delegationAsOf < d."ValidUntil"
                      AND d."AcceptanceState" IN ('notRequired', 'accepted')
                    ORDER BY d."ValidFrom" DESC, d."Id" DESC
                    LIMIT 1
                ) delegation ON TRUE
                {where}
            )
            """;

        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"{eligibleCte} SELECT COUNT(*) AS \"Value\" FROM eligible WHERE inbox_rank = 1",
                BuildParameters(args))
            .SingleAsync(cancellationToken);

        // There cannot be a page when the authoritative eligible count is zero,
        // so avoid issuing the projection query for an empty inbox.
        if (totalCount == 0)
        {
            return new PagedResult<InboxListItem>([], page, pageSize, totalCount);
        }

        var skip = (page - 1) * pageSize;
        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("pendingTask", UserTaskStatuses.Pending),
            ("cancelledTask", UserTaskStatuses.Cancelled),
            ("take", pageSize),
            ("skip", skip)
        };
        var rows = await dbContext.Database
            .SqlQueryRaw<InboxPageRow>(
                $"""
                {eligibleCte},
                page_task_ids AS MATERIALIZED (
                    SELECT e."Id", e."InstanceId", e."MultiInstanceExecutionId",
                           e."DelegationId", e."ActingFor",
                           e."TaskCreatedAt", e."TaskUpdatedAt",
                           e."InstanceCreatedAt", e."InstanceUpdatedAt"
                    FROM eligible e
                    WHERE e.inbox_rank = 1
                    ORDER BY {eligibleOrderBy}
                    LIMIT @take OFFSET @skip
                ),
                page_instances AS (
                    SELECT DISTINCT page."InstanceId"
                    FROM page_task_ids page
                ),
                latest_variables AS (
                    SELECT DISTINCT ON (v."InstanceId", v."VariableName")
                           v."InstanceId", v."VariableName", v."ValueJson"
                    FROM flowbit.instance_variables v
                    JOIN page_instances page ON page."InstanceId" = v."InstanceId"
                    ORDER BY v."InstanceId", v."VariableName", v."Id" DESC
                ),
                variable_values AS (
                    SELECT v."InstanceId",
                           jsonb_object_agg(v."VariableName", v."ValueJson") AS "VariablesJson"
                    FROM latest_variables v
                    GROUP BY v."InstanceId"
                ),
                page_executions AS (
                    SELECT DISTINCT page."MultiInstanceExecutionId" AS "ExecutionId"
                    FROM page_task_ids page
                    WHERE page."MultiInstanceExecutionId" IS NOT NULL
                ),
                mi_task_counts AS (
                    SELECT task."MultiInstanceExecutionId" AS "ExecutionId",
                           (COUNT(*) FILTER (WHERE task."Status" = @activeTask))::integer AS "ActiveCount",
                           (COUNT(*) FILTER (WHERE task."Status" = @pendingTask))::integer AS "PendingCount",
                           (COUNT(*) FILTER (WHERE task."Status" = @cancelledTask))::integer AS "CancelledCount"
                    FROM flowbit.user_tasks task
                    JOIN page_executions page
                      ON page."ExecutionId" = task."MultiInstanceExecutionId"
                    GROUP BY task."MultiInstanceExecutionId"
                ),
                mi_flow_counts AS (
                    SELECT flow."ExecutionId",
                           jsonb_object_agg(
                               flow."FlowId"::text,
                               flow."CompletedCount"
                               ORDER BY flow."FlowId") AS "FlowCountsJson"
                    FROM flowbit.multi_instance_flow_counts flow
                    JOIN page_executions page ON page."ExecutionId" = flow."ExecutionId"
                    GROUP BY flow."ExecutionId"
                )
                SELECT w."Id" AS "InstanceId",
                       wd."Id" AS "WorkflowId",
                       w."WorkflowDefinitionId" AS "WorkflowDefinitionId",
                       wd."Name" AS "WorkflowName",
                       wd."Version" AS "WorkflowVersion",
                       w."BusinessKey" AS "BusinessKey",
                       w."BusinessKeyUniqueness" AS "BusinessKeyUniqueness",
                       token."Id" AS "TokenId",
                       ut."Id" AS "UserTaskId",
                       ut."MultiInstanceExecutionId" AS "MultiInstanceExecutionId",
                       ut."ItemIndex" AS "ItemIndex",
                       ut."ItemValueJson"::text AS "ItemValueJson",
                       ut."Assignee" AS "Assignee",
                       ut."NodeId" AS "CurrentNodeId",
                       ut."NodeName" AS "CurrentNodeName",
                       ut."NodeExternalId" AS "CurrentNodeExternalId",
                       token."NodeType" AS "CurrentNodeType",
                       ut."Roles" AS "CurrentNodeRoles",
                       ut."RequiresClaim" AS "CurrentRequiresClaim",
                       ut."RequiresAssignment" AS "CurrentRequiresAssignment",
                       w."Status" AS "Status",
                       ut."ClaimedBy" AS "ClaimedBy",
                       page."DelegationId" AS "DelegationId",
                       page."ActingFor" AS "ActingFor",
                       w."StartedBy" AS "StartedBy",
                       ut."CreatedAt" AS "TaskCreatedAt",
                       ut."UpdatedAt" AS "TaskUpdatedAt",
                       w."CreatedAt" AS "InstanceCreatedAt",
                       w."UpdatedAt" AS "InstanceUpdatedAt",
                       COALESCE(values."VariablesJson", jsonb_build_object())::text AS "VariablesJson",
                       mie."Id" AS "MiId",
                       mie."InstanceId" AS "MiInstanceId",
                       mie."TokenId" AS "MiTokenId",
                       mie."NodeId" AS "MiNodeId",
                       mie."Mode" AS "MiMode",
                       mie."Source" AS "MiSource",
                       mie."OnePerActor" AS "MiOnePerActor",
                       mie."ResultVariable" AS "MiResultVariable",
                       mie."Status" AS "MiStatus",
                       mie."TotalCount" AS "MiTotalCount",
                       mie."CompletedCount" AS "MiCompletedCount",
                       mie."CancelledCount" AS "MiCancelledCount",
                       mie."WinningFlowId" AS "MiWinningFlowId",
                       mie."CompletionReason" AS "MiCompletionReason",
                       mie."CreatedAt" AS "MiCreatedAt",
                       mie."UpdatedAt" AS "MiUpdatedAt",
                       mie."CompletedAt" AS "MiCompletedAt",
                       COALESCE(task_counts."ActiveCount", 0) AS "MiActiveTaskCount",
                       COALESCE(task_counts."PendingCount", 0) AS "MiPendingTaskCount",
                       COALESCE(task_counts."CancelledCount", 0) AS "MiCancelledTaskCount",
                       COALESCE(flow_counts."FlowCountsJson", jsonb_build_object())::text AS "MiFlowCountsJson"
                FROM page_task_ids page
                JOIN flowbit.user_tasks ut ON ut."Id" = page."Id"
                JOIN flowbit.workflow_instances w ON w."Id" = ut."InstanceId"
                JOIN flowbit.workflow_definitions wd ON wd."Id" = w."WorkflowDefinitionId"
                JOIN flowbit.execution_tokens token ON token."Id" = ut."TokenId"
                LEFT JOIN variable_values values ON values."InstanceId" = w."Id"
                LEFT JOIN flowbit.multi_instance_executions mie
                       ON mie."Id" = ut."MultiInstanceExecutionId"
                LEFT JOIN mi_task_counts task_counts
                       ON task_counts."ExecutionId" = mie."Id"
                LEFT JOIN mi_flow_counts flow_counts
                       ON flow_counts."ExecutionId" = mie."Id"
                ORDER BY {pageOrderBy}
                """,
                BuildParameters(pageArgs))
            .ToListAsync(cancellationToken);

        var items = rows.Select(ToInboxListItem).ToList();
        return new PagedResult<InboxListItem>(items, page, pageSize, totalCount);
    }

    public async Task<PagedResult<ManagedUserTaskRecord>> ListManageableUserTasksAsync(
        IReadOnlyCollection<string> managerRoles,
        long? taskId,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        string? owner,
        string? ownership,
        IReadOnlyList<VariableFilter> variableFilters,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var lowerRoles = managerRoles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
        var where = new StringBuilder("""
            WHERE w."Status" = @runningInstance
              AND ut."Status" = @activeTask
              AND token."Status" = @activeToken
              AND (mie."Id" IS NULL OR mie."Status" = @activeExecution)
              AND EXISTS (
                    SELECT 1
                    FROM jsonb_array_elements_text(
                        CASE
                            WHEN jsonb_typeof(d."Definition" -> 'taskAssignmentRoles') = 'array'
                            THEN d."Definition" -> 'taskAssignmentRoles'
                            ELSE '[]'::jsonb
                        END) AS manager_role
                    WHERE lower(manager_role) = ANY(@lowerManagerRoles)
                  )
            """);
        var args = new List<(string Name, object Value)>
        {
            ("runningInstance", WorkflowInstanceStatuses.Running),
            ("activeTask", UserTaskStatuses.Active),
            ("activeToken", ExecutionTokenStatuses.Active),
            ("activeExecution", MultiInstanceExecutionStatuses.Active),
            ("lowerManagerRoles", lowerRoles)
        };

        if (taskId is not null)
        {
            args.Add(("taskId", taskId.Value));
            where.Append(" AND ut.\"Id\" = @taskId");
        }
        AppendInstanceIdFilter(where, args, instanceId);
        AppendWorkflowIdFilter(where, args, workflowId);
        AppendWorkflowKeyFilter(where, args, workflowKey);
        AppendBusinessKeyFilter(where, args, businessKey);
        AppendNodeIdFilter(where, args, nodeId, useUserTaskProjection: true);
        AppendNodeExternalIdFilter(where, args, nodeExternalId, useUserTaskProjection: true);
        AppendVariableFilters(where, args, variableFilters);

        if (!string.IsNullOrWhiteSpace(owner))
        {
            args.Add(("owner", owner.Trim()));
            where.Append(" AND lower(COALESCE(ut.\"Assignee\", ut.\"ClaimedBy\")) = lower(@owner)");
        }

        switch (ownership)
        {
            case UserTaskOwnershipKinds.Assigned:
                where.Append(" AND ut.\"Assignee\" IS NOT NULL");
                break;
            case UserTaskOwnershipKinds.Claimed:
                where.Append(" AND ut.\"Assignee\" IS NULL AND ut.\"ClaimedBy\" IS NOT NULL");
                break;
            case UserTaskOwnershipKinds.Unassigned:
                where.Append(" AND ut.\"Assignee\" IS NULL AND ut.\"ClaimedBy\" IS NULL");
                break;
        }

        const string from = """
            FROM flowbit.user_tasks ut
            JOIN flowbit.workflow_instances w ON w."Id" = ut."InstanceId"
            JOIN flowbit.workflow_definitions d ON d."Id" = w."WorkflowDefinitionId"
            JOIN flowbit.execution_tokens token ON token."Id" = ut."TokenId"
            LEFT JOIN flowbit.multi_instance_executions mie ON mie."Id" = ut."MultiInstanceExecutionId"
            """;
        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS \"Value\" {from} {where}",
                BuildParameters(args))
            .SingleAsync(cancellationToken);

        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("take", pageSize),
            ("skip", (page - 1) * pageSize)
        };
        var tasks = await dbContext.UserTasks
            .FromSqlRaw(
                $"SELECT ut.* {from} {where} ORDER BY ut.\"UpdatedAt\" DESC, ut.\"Id\" DESC LIMIT @take OFFSET @skip",
                BuildParameters(pageArgs))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new PagedResult<ManagedUserTaskRecord>(
            await ToManagedUserTaskRecordsAsync(tasks, false, cancellationToken),
            page,
            pageSize,
            totalCount);
    }

    public async Task<PagedResult<ManagedUserTaskRecord>> ListDistributableUserTasksAsync(
        string workflowKey,
        long? taskId,
        long? instanceId,
        long? workflowId,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        string? owner,
        string? ownership,
        IReadOnlyList<VariableFilter> variableFilters,
        bool includeVariables,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var where = new StringBuilder("""
            WHERE w."Status" = @runningInstance
              AND w."WorkflowKey" = @distributionWorkflowKey
              AND ut."Status" = @activeTask
              AND token."Status" = @activeToken
              AND (mie."Id" IS NULL OR mie."Status" = @activeExecution)
            """);
        var args = new List<(string Name, object Value)>
        {
            ("runningInstance", WorkflowInstanceStatuses.Running),
            ("distributionWorkflowKey", workflowKey),
            ("activeTask", UserTaskStatuses.Active),
            ("activeToken", ExecutionTokenStatuses.Active),
            ("activeExecution", MultiInstanceExecutionStatuses.Active)
        };

        if (taskId is not null)
        {
            args.Add(("taskId", taskId.Value));
            where.Append(" AND ut.\"Id\" = @taskId");
        }
        AppendInstanceIdFilter(where, args, instanceId);
        AppendWorkflowIdFilter(where, args, workflowId);
        AppendBusinessKeyFilter(where, args, businessKey);
        AppendNodeIdFilter(where, args, nodeId, useUserTaskProjection: true);
        AppendNodeExternalIdFilter(where, args, nodeExternalId, useUserTaskProjection: true);
        AppendVariableFilters(where, args, variableFilters);

        if (!string.IsNullOrWhiteSpace(owner))
        {
            args.Add(("owner", owner.Trim()));
            where.Append(" AND lower(COALESCE(ut.\"Assignee\", ut.\"ClaimedBy\")) = lower(@owner)");
        }

        switch (ownership)
        {
            case UserTaskOwnershipKinds.Assigned:
                where.Append(" AND ut.\"Assignee\" IS NOT NULL");
                break;
            case UserTaskOwnershipKinds.Claimed:
                where.Append(" AND ut.\"Assignee\" IS NULL AND ut.\"ClaimedBy\" IS NOT NULL");
                break;
            case UserTaskOwnershipKinds.Unassigned:
                where.Append(" AND ut.\"Assignee\" IS NULL AND ut.\"ClaimedBy\" IS NULL");
                break;
        }

        const string from = """
            FROM flowbit.user_tasks ut
            JOIN flowbit.workflow_instances w ON w."Id" = ut."InstanceId"
            JOIN flowbit.execution_tokens token ON token."Id" = ut."TokenId"
            LEFT JOIN flowbit.multi_instance_executions mie ON mie."Id" = ut."MultiInstanceExecutionId"
            """;
        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS \"Value\" {from} {where}",
                BuildParameters(args))
            .SingleAsync(cancellationToken);

        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("take", pageSize),
            ("skip", (page - 1) * pageSize)
        };
        var tasks = await dbContext.UserTasks
            .FromSqlRaw(
                $"SELECT ut.* {from} {where} ORDER BY ut.\"UpdatedAt\" DESC, ut.\"Id\" DESC LIMIT @take OFFSET @skip",
                BuildParameters(pageArgs))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return new PagedResult<ManagedUserTaskRecord>(
            await ToManagedUserTaskRecordsAsync(tasks, includeVariables, cancellationToken),
            page,
            pageSize,
            totalCount);
    }

    private static (StringBuilder Where, List<(string Name, object Value)> Args) BuildInboxWhere(
        string user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset asOf,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters)
    {
        // Roles are matched case-insensitively (mirrors the in-memory role check),
        // so compare lower-cased node roles against lower-cased actor roles.
        var lowerRoles = roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();

        // Actor-scoped inbox predicate (running user tasks the caller may see/act on).
        // Aliased as w so the variable EXISTS filters can correlate on w."Id".
        var where = new StringBuilder("""
             WHERE w."Status" = @status
              AND ut."Status" = @activeTask
              AND (NOT ut."RequiresAssignment" OR ut."Assignee" IS NOT NULL)
              AND (
                    cardinality(ut."Roles") = 0
                 OR EXISTS (
                      SELECT 1 FROM unnest(ut."Roles") AS node_role
                      WHERE lower(node_role) = ANY(@lowerRoles)
                    )
                  )
              AND (
                    lower(ut."Assignee") = lower(@user)
                 OR (ut."Assignee" IS NULL AND (
                      lower(ut."ClaimedBy") = lower(@user)
                   OR (
                      NOT (ut."RequiresClaim"
                               AND ut."ClaimedBy" IS NOT NULL
                               AND lower(ut."ClaimedBy") <> lower(@user))
                     )
                   OR EXISTS (
                      SELECT 1
                      FROM jsonb_array_elements(
                          CASE
                              WHEN jsonb_typeof(wd."Definition"->'sequenceFlows') = 'array'
                              THEN wd."Definition"->'sequenceFlows'
                              ELSE '[]'::jsonb
                          END) AS bypass_flow
                      WHERE (bypass_flow->>'sourceRef')::integer = ut."NodeId"
                        AND COALESCE((bypass_flow->>'isSelectable')::boolean, TRUE)
                        AND NOT COALESCE((bypass_flow->>'isDefault')::boolean, FALSE)
                        AND COALESCE((bypass_flow->>'canActWithoutClaim')::boolean, FALSE)
                     )
                    ))
                 OR delegation."Id" IS NOT NULL
                  )
              AND (
                    NOT COALESCE(mie."OnePerActor", FALSE)
                 OR NOT EXISTS (
                      SELECT 1
                      FROM flowbit.user_tasks completed
                      WHERE completed."MultiInstanceExecutionId" = mie."Id"
                        AND completed."Status" = @completedTask
                        AND completed."CompletedBy" IS NOT NULL
                        AND lower(COALESCE(
                                completed."CompletedActingFor",
                                completed."CompletedBy")) =
                            lower(COALESCE(delegation."Delegator", @user))
                    )
                  )
            """);

        var args = new List<(string Name, object Value)>
        {
            ("status", WorkflowInstanceStatuses.Running),
            ("activeTask", UserTaskStatuses.Active),
            ("completedTask", UserTaskStatuses.Completed),
            ("user", user),
            ("delegationAsOf", asOf),
            ("lowerRoles", lowerRoles)
        };

        AppendInstanceIdFilter(where, args, instanceId);
        AppendWorkflowIdFilter(where, args, workflowId);
        AppendWorkflowKeyFilter(where, args, workflowKey);
        AppendBusinessKeyFilter(where, args, businessKey);
        AppendNodeIdFilter(where, args, nodeId, useUserTaskProjection: true);
        AppendNodeExternalIdFilter(where, args, nodeExternalId, useUserTaskProjection: true);
        AppendVariableFilters(where, args, variableFilters);

        return (where, args);
    }
#pragma warning restore EF1002

    // Filters on the instance id (primary key). The value is parameter-bound,
    // so there is no SQL injection surface.
    private static void AppendInstanceIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        long? instanceId)
    {
        if (instanceId is null)
        {
            return;
        }

        args.Add(("instanceId", instanceId.Value));
        where.Append(" AND w.\"Id\" = @instanceId");
    }

    // Filters on the owning workflow definition id. The value is parameter-bound,
    // so there is no SQL injection surface.
    private static void AppendWorkflowIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        long? workflowId)
    {
        if (workflowId is null)
        {
            return;
        }

        args.Add(("workflowId", workflowId.Value));
        where.Append(" AND w.\"WorkflowDefinitionId\" = @workflowId");
    }

    // Filters on the stable, cross-version workflow key (the JSON model id stored on
    // workflow_definitions), matched via a correlated EXISTS against the instance's
    // definition. Because every version shares the key, this spans all versions. The
    // value is parameter-bound, so there is no SQL injection surface.
    private static void AppendWorkflowKeyFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        string? workflowKey)
    {
        if (workflowKey is null)
        {
            return;
        }

        args.Add(("workflowKey", workflowKey));
        where.Append(
            " AND EXISTS (SELECT 1 FROM flowbit.workflow_definitions d" +
            " WHERE d.\"Id\" = w.\"WorkflowDefinitionId\" AND d.\"WorkflowKey\" = @workflowKey)");
    }

    // Business keys are normalized at start and stored with PostgreSQL's
    // deterministic C collation, so this is an exact, case-sensitive match.
    private static void AppendBusinessKeyFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        string? businessKey)
    {
        if (string.IsNullOrWhiteSpace(businessKey))
        {
            return;
        }

        args.Add(("businessKey", businessKey.Trim()));
        where.Append(" AND w.\"BusinessKey\" = @businessKey");
    }

    // Filters on the token/task node id. The value is parameter-bound,
    // so there is no SQL injection surface.
    private static void AppendNodeIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        int? nodeId,
        bool useUserTaskProjection = false)
    {
        if (nodeId is null)
        {
            return;
        }

        args.Add(("nodeId", nodeId.Value));
        where.Append(useUserTaskProjection
            ? " AND ut.\"NodeId\" = @nodeId"
            : """
               AND EXISTS (
                    SELECT 1
                    FROM flowbit.execution_tokens position
                    WHERE position."InstanceId" = w."Id"
                      AND position."Status" <> 'merged'
                      AND position."NodeId" = @nodeId
                  )
              """);
    }

    // Filters on the projected token/task externalId (exact, case-insensitive).
    // The value is parameter-bound, so there is no SQL injection surface.
    private static void AppendNodeExternalIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        string? nodeExternalId,
        bool useUserTaskProjection = false)
    {
        if (string.IsNullOrWhiteSpace(nodeExternalId))
        {
            return;
        }

        args.Add(("nodeExternalId", nodeExternalId.Trim()));
        where.Append(useUserTaskProjection
            ? " AND lower(ut.\"NodeExternalId\") = lower(@nodeExternalId)"
            : """
               AND EXISTS (
                    SELECT 1
                    FROM flowbit.execution_tokens position
                    WHERE position."InstanceId" = w."Id"
                      AND position."Status" <> 'merged'
                      AND lower(position."NodeExternalId") = lower(@nodeExternalId)
                  )
              """);
    }

    // Appends one correlated lookup per filter. Only the newest row for the
    // variable participates, and its scalar value must equal the target
    // case-insensitively. A latest array/object value never matches, and an older
    // scalar value cannot match through it.
    // Names/values bind as parameters, so there is no SQL injection surface.
    // `#>> ARRAY[]::text[]` extracts the root scalar as text (equivalent to the
    // `#>> '{}'` literal but brace-free, since FromSqlRaw runs string.Format
    // over the SQL and would treat literal braces as format placeholders).
    private static void AppendVariableFilters(
        StringBuilder where,
        List<(string Name, object Value)> args,
        IReadOnlyList<VariableFilter> filters)
    {
        for (var i = 0; i < filters.Count; i++)
        {
            args.Add(($"vn{i}", filters[i].Name));
            args.Add(($"vv{i}", filters[i].Value));
            where.Append(
                $" AND (SELECT CASE WHEN jsonb_typeof(v.\"ValueJson\") NOT IN ('array', 'object')" +
                $" THEN lower(v.\"ValueJson\" #>> ARRAY[]::text[]) END" +
                $" FROM flowbit.instance_variables v WHERE v.\"InstanceId\" = w.\"Id\"" +
                $" AND v.\"VariableName\" = @vn{i} ORDER BY v.\"Id\" DESC LIMIT 1) = lower(@vv{i})");
        }
    }

    private static NpgsqlParameter[] BuildParameters(IEnumerable<(string Name, object Value)> args) =>
        args.Select(a => new NpgsqlParameter(a.Name, a.Value)).ToArray();

    private static string BuildInstanceOrderBy(IReadOnlyList<InstanceSortCriterion> requested)
    {
        IReadOnlyList<InstanceSortCriterion> sort = requested.Count == 0
            ? [new InstanceSortCriterion(InstanceSortField.UpdatedAt, SortDirection.Descending)]
            : requested;
        var parts = sort.Select(criterion =>
        {
            var column = criterion.Field switch
            {
                InstanceSortField.Id => "w.\"Id\"",
                InstanceSortField.CreatedAt => "w.\"CreatedAt\"",
                InstanceSortField.UpdatedAt => "w.\"UpdatedAt\"",
                _ => throw new ArgumentOutOfRangeException(nameof(requested), criterion.Field, "Unsupported instance sort field.")
            };
            return $"{column} {ToSqlDirection(criterion.Direction)}";
        }).ToList();

        if (!sort.Any(criterion => criterion.Field == InstanceSortField.Id))
        {
            parts.Add($"w.\"Id\" {ToSqlDirection(sort[^1].Direction)}");
        }

        return string.Join(", ", parts);
    }

    private static string BuildInboxOrderBy(IReadOnlyList<InboxSortCriterion> requested, string alias)
    {
        IReadOnlyList<InboxSortCriterion> sort = requested.Count == 0
            ? [new InboxSortCriterion(InboxSortField.TaskUpdatedAt, SortDirection.Descending)]
            : requested;
        var parts = sort.Select(criterion =>
        {
            var column = criterion.Field switch
            {
                InboxSortField.UserTaskId => $"{alias}.\"Id\"",
                InboxSortField.InstanceId => $"{alias}.\"InstanceId\"",
                InboxSortField.TaskCreatedAt => $"{alias}.\"TaskCreatedAt\"",
                InboxSortField.TaskUpdatedAt => $"{alias}.\"TaskUpdatedAt\"",
                InboxSortField.InstanceCreatedAt => $"{alias}.\"InstanceCreatedAt\"",
                InboxSortField.InstanceUpdatedAt => $"{alias}.\"InstanceUpdatedAt\"",
                _ => throw new ArgumentOutOfRangeException(nameof(requested), criterion.Field, "Unsupported inbox sort field.")
            };
            return $"{column} {ToSqlDirection(criterion.Direction)}";
        }).ToList();

        if (!sort.Any(criterion => criterion.Field == InboxSortField.UserTaskId))
        {
            parts.Add($"{alias}.\"Id\" {ToSqlDirection(sort[^1].Direction)}");
        }

        return string.Join(", ", parts);
    }

    private static string ToSqlDirection(SortDirection direction) => direction switch
    {
        SortDirection.Ascending => "ASC",
        SortDirection.Descending => "DESC",
        _ => throw new ArgumentOutOfRangeException(nameof(direction), direction, "Unsupported sort direction.")
    };

    private async Task<IReadOnlyList<InstanceListItem>> ToListItemsAsync(
        IReadOnlyList<WorkflowInstanceEntity> entities,
        bool includeVariables,
        CancellationToken cancellationToken)
    {
        if (entities.Count == 0)
        {
            return [];
        }

        // Fetch only the definition name/version (never the JSONB body) for the
        // bounded set of definitions referenced by this page.
        var definitionIds = entities.Select(e => e.WorkflowDefinitionId).Distinct().ToList();
        var definitions = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(d => definitionIds.Contains(d.Id))
            .Select(d => new { d.Id, d.Name, d.Version })
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var instanceIds = entities.Select(e => e.Id).Distinct().ToList();
        var tokens = await dbContext.ExecutionTokens.AsNoTracking()
            .Where(t => instanceIds.Contains(t.InstanceId))
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);
        var tokensByInstance = tokens
            .GroupBy(t => t.InstanceId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<ExecutionTokenEntity>)g.ToList());
        var taskSummaries = await GetUserTaskWorkSummariesAsync(instanceIds, cancellationToken);
        var variablesByInstance = includeVariables
            ? await GetLatestVariableValuesAsync(instanceIds, cancellationToken)
            : null;

        return entities.Select(e =>
        {
            definitions.TryGetValue(e.WorkflowDefinitionId, out var definition);
            if (!tokensByInstance.TryGetValue(e.Id, out var instanceTokens)
                || SelectRepresentativeToken(e.Status, instanceTokens) is not { } token)
            {
                throw new InvalidOperationException($"Workflow instance #{e.Id} has no execution token.");
            }
            taskSummaries.TryGetValue(e.Id, out var taskSummary);
            IReadOnlyDictionary<string, System.Text.Json.JsonElement>? variables = null;
            if (variablesByInstance is not null)
            {
                variables = variablesByInstance.TryGetValue(e.Id, out var values)
                    ? values
                    : new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
            }

            return new InstanceListItem(
                e.Id,
                definition?.Id ?? 0,
                e.WorkflowDefinitionId,
                definition?.Name ?? string.Empty,
                definition?.Version ?? 0,
                e.BusinessKey,
                e.BusinessKeyUniqueness,
                token.Id,
                null,
                null,
                null,
                null,
                null,
                token.NodeId,
                token.NodeName,
                token.NodeExternalId,
                token.NodeType,
                [],
                false,
                false,
                e.Status,
                taskSummary?.SoleClaimedBy,
                e.StartedBy,
                e.CreatedAt,
                e.UpdatedAt,
                taskSummary,
                variables,
                null,
                token.FaultCode,
                token.FaultDescription);
        }).ToList();
    }

    private async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, System.Text.Json.JsonElement>>>
        GetLatestVariableValuesAsync(
            IReadOnlyCollection<long> instanceIds,
            CancellationToken cancellationToken)
    {
        var ids = instanceIds.ToArray();
        var rows = await dbContext.InstanceVariables
            .FromSqlInterpolated($"""
                SELECT DISTINCT ON (v."InstanceId", v."VariableName") v.*
                FROM flowbit.instance_variables AS v
                WHERE v."InstanceId" = ANY ({ids})
                ORDER BY v."InstanceId", v."VariableName", v."Id" DESC
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(variable => variable.InstanceId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<string, System.Text.Json.JsonElement>)group.ToDictionary(
                    variable => variable.VariableName,
                    variable => variable.ValueJson.RootElement.Clone(),
                    StringComparer.OrdinalIgnoreCase));
    }

    public async Task<WorkflowInstanceRecord?> GetInstanceAsync(long id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowInstances.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var tokenQuery = dbContext.ExecutionTokens.AsNoTracking()
            .Where(token => token.InstanceId == id
                            && token.Status != ExecutionTokenStatuses.Merged);
        tokenQuery = entity.Status switch
        {
            WorkflowInstanceStatuses.Running => tokenQuery
                .Where(token => token.Status == ExecutionTokenStatuses.Active)
                .OrderBy(token => token.Id),
            WorkflowInstanceStatuses.Faulted => tokenQuery
                .Where(token => token.Status == ExecutionTokenStatuses.Faulted)
                .OrderByDescending(token => token.UpdatedAt)
                .ThenByDescending(token => token.Id),
            WorkflowInstanceStatuses.Completed => tokenQuery
                .Where(token => token.Status == ExecutionTokenStatuses.Completed)
                .OrderByDescending(token =>
                    token.TerminationReason == ExecutionTokenTerminationReasons.TerminateEnd)
                .ThenByDescending(token => token.UpdatedAt)
                .ThenByDescending(token => token.Id),
            WorkflowInstanceStatuses.Cancelled => tokenQuery
                .Where(token => token.Status == ExecutionTokenStatuses.Cancelled)
                .OrderByDescending(token => token.UpdatedAt)
                .ThenByDescending(token => token.Id),
            _ => tokenQuery
                .OrderByDescending(token => token.UpdatedAt)
                .ThenByDescending(token => token.Id)
        };
        var token = await tokenQuery.FirstOrDefaultAsync(cancellationToken);
        token ??= await dbContext.ExecutionTokens.AsNoTracking()
            .Where(candidate => candidate.InstanceId == id)
            .OrderByDescending(candidate => candidate.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var activeTasks = await dbContext.UserTasks.AsNoTracking()
            .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        var task = activeTasks.Count == 1 ? activeTasks[0] : null;
        return token is null ? null : ToRecord(entity, token, task);
    }

    public async Task<string?> GetInstanceStatusAsync(
        long id,
        CancellationToken cancellationToken)
    {
        var tracked = dbContext.WorkflowInstances.Local.SingleOrDefault(instance =>
            instance.Id == id);
        if (tracked is not null)
        {
            return tracked.Status;
        }
        return await dbContext.WorkflowInstances.AsNoTracking()
            .Where(instance => instance.Id == id)
            .Select(instance => instance.Status)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<WorkflowInstanceRecord?> GetInstanceForUpdateAsync(
        long id,
        bool lockActiveUserTask,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowInstances
            .FromSqlInterpolated($"SELECT * FROM flowbit.workflow_instances WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return null;
        }

        // Preserve the mutation lock hierarchy for every instance transaction:
        // instance -> active gateway executions/states/branches -> active tokens -> multi-instance
        // executions -> user tasks. Stable id ordering also keeps independently
        // addressed task/branch operations from introducing lock-order cycles.
        _ = await dbContext.GatewayExecutions
            .FromSqlInterpolated($"SELECT * FROM flowbit.gateway_executions WHERE \"InstanceId\" = {id} AND \"Status\" = {GatewayExecutionStatuses.Active} ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(cancellationToken);
        _ = await dbContext.ComplexGatewayStates
            .FromSqlInterpolated($"""
                SELECT *
                FROM flowbit.complex_gateway_states
                WHERE "InstanceId" = {id}
                ORDER BY "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        _ = await dbContext.GatewayBranches
            .FromSqlInterpolated($"""
                SELECT *
                FROM flowbit.gateway_branches
                WHERE "ExecutionId" IN (
                    SELECT "Id"
                    FROM flowbit.gateway_executions
                    WHERE "InstanceId" = {id} AND "Status" = {GatewayExecutionStatuses.Active})
                  AND "Status" = {GatewayBranchStatuses.Active}
                ORDER BY "Id"
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        var tokens = await dbContext.ExecutionTokens
            .FromSqlInterpolated($"SELECT * FROM flowbit.execution_tokens WHERE \"InstanceId\" = {id} AND \"Status\" = {ExecutionTokenStatuses.Active} ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(cancellationToken);
        _ = await dbContext.MultiInstanceExecutions
            .FromSqlInterpolated($"SELECT * FROM flowbit.multi_instance_executions WHERE \"InstanceId\" = {id} AND \"Status\" = {MultiInstanceExecutionStatuses.Active} ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(cancellationToken);
        var token = SelectRepresentativeToken(entity.Status, tokens);
        if (token is null)
        {
            token = await dbContext.ExecutionTokens.AsNoTracking()
                .Where(candidate => candidate.InstanceId == id)
                .OrderByDescending(candidate => candidate.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        UserTaskEntity? task = null;
        if (lockActiveUserTask)
        {
            var activeTasks = await dbContext.UserTasks
                .FromSqlInterpolated($"SELECT * FROM flowbit.user_tasks WHERE \"InstanceId\" = {id} AND \"Status\" = {UserTaskStatuses.Active} ORDER BY \"Id\" FOR UPDATE")
                .ToListAsync(cancellationToken);
            // Singular compatibility callers must not silently bind to an
            // arbitrary branch when parallel execution exposes multiple tasks.
            task = activeTasks.Count == 1 ? activeTasks[0] : null;
        }
        return token is null ? null : ToRecord(entity, token, task);
    }

    public async Task<ExecutionTokenRecord?> GetExecutionTokenAsync(
        long tokenId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var tracked = dbContext.ExecutionTokens.Local.SingleOrDefault(entity => entity.Id == tokenId);
        if (tracked is not null)
        {
            return ToRecord(tracked);
        }
        var entity = forUpdate
            ? await dbContext.ExecutionTokens
                .FromSqlInterpolated($"SELECT * FROM flowbit.execution_tokens WHERE \"Id\" = {tokenId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.ExecutionTokens.AsNoTracking()
                .SingleOrDefaultAsync(token => token.Id == tokenId, cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<ExecutionTokenRecord>> GetExecutionTokensAsync(
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return [];
        }

        var distinctIds = tokenIds.Distinct().ToArray();
        var local = dbContext.ExecutionTokens.Local
            .Where(token => distinctIds.Contains(token.Id))
            .ToList();
        var localIds = local.Select(token => token.Id).ToHashSet();
        var missingIds = distinctIds.Where(id => !localIds.Contains(id)).ToArray();
        var persisted = missingIds.Length == 0
            ? []
            : await dbContext.ExecutionTokens
                .Where(token => missingIds.Contains(token.Id))
                .OrderBy(token => token.Id)
                .ToListAsync(cancellationToken);
        return persisted
            .Concat(local)
            .Distinct()
            .OrderBy(token => token.Id)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<IReadOnlyList<ExecutionTokenRecord>> ListExecutionTokensAsync(
        long instanceId,
        string? status,
        CancellationToken cancellationToken)
    {
        IQueryable<ExecutionTokenEntity> query =
            status == ExecutionTokenStatuses.Active
                ? dbContext.ExecutionTokens
                : dbContext.ExecutionTokens.AsNoTracking();
        query = query
            .Where(token => token.InstanceId == instanceId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(token => token.Status == status);
        }
        var entities = await query
            .OrderBy(token => token.Id)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(token => token.Id);
        foreach (var tracked in dbContext.ExecutionTokens.Local.Where(token => token.InstanceId == instanceId))
        {
            byId[tracked.Id] = tracked;
        }

        return byId.Values
            .Where(token => string.IsNullOrWhiteSpace(status) || token.Status == status)
            .OrderBy(token => token.Id)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<IReadOnlyList<ExecutionTokenRecord>> ListCurrentExecutionTokensAsync(
        long instanceId,
        long representativeTokenId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.ExecutionTokens.AsNoTracking()
            .Where(token =>
                token.InstanceId == instanceId
                && (token.Status == ExecutionTokenStatuses.Active
                    || token.Id == representativeTokenId))
            .OrderBy(token => token.Id)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(token => token.Id);
        foreach (var tracked in dbContext.ExecutionTokens.Local.Where(token =>
                     token.InstanceId == instanceId))
        {
            byId[tracked.Id] = tracked;
        }
        return byId.Values
            .OrderBy(token => token.Id)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<ExecutionTokenRecord> AddExecutionTokenAsync(
        long instanceId,
        CurrentNodeSnapshot node,
        long? gatewayBranchId,
        int? arrivedViaFlowId,
        NodeExecutionActorRecord triggeredBy,
        CancellationToken cancellationToken)
    {
        var instance = dbContext.WorkflowInstances.Local.SingleOrDefault(entity => entity.Id == instanceId)
            ?? await dbContext.WorkflowInstances.SingleAsync(entity => entity.Id == instanceId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var token = NewToken(instance, node, now);
        token.GatewayBranchId = gatewayBranchId;
        token.ArrivedViaFlowId = arrivedViaFlowId;
        dbContext.ExecutionTokens.Add(token);
        await dbContext.SaveChangesAsync(cancellationToken);
        UserTaskEntity? task = null;
        if (node.Type == BpmnFlowNodeTypes.UserTask && !node.IsMultiInstance)
        {
            task = NewUserTask(instance, token, node, now);
            dbContext.UserTasks.Add(task);
        }
        var nodeExecution = NewNodeExecution(
            instance,
            token,
            node,
            NodeExecutionKinds.Node,
            NodeExecutionStatuses.Active,
            gatewayBranchId,
            arrivedViaFlowId,
            triggeredBy,
            now,
            task);
        dbContext.NodeExecutions.Add(nodeExecution);
        token.CurrentNodeExecution = nodeExecution;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(token);
    }

    public async Task<IReadOnlyList<ExecutionTokenRecord>> AddGatewayBranchTokensAsync(
        long instanceId,
        CurrentNodeSnapshot gateway,
        long? parentBranchId,
        IReadOnlyList<long> gatewayBranchIds,
        IReadOnlyCollection<long> complexDrainStateIds,
        NodeExecutionActorRecord triggeredBy,
        CancellationToken cancellationToken)
    {
        if (gatewayBranchIds.Count == 0)
        {
            return [];
        }

        var instance = dbContext.WorkflowInstances.Local.SingleOrDefault(entity => entity.Id == instanceId)
            ?? await dbContext.WorkflowInstances.SingleAsync(
                entity => entity.Id == instanceId,
                cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var tokens = gatewayBranchIds
            .Select(branchId =>
            {
                var token = NewToken(instance, gateway, now);
                // The spawned token already belongs to its child branch so an
                // immediate scoped interrupt can see/cancel it. Its gateway
                // node-execution entry snapshot still records parentBranchId;
                // AdvanceAutomaticTokenAsync records the child as the exit.
                token.GatewayBranchId = branchId;
                token.ComplexDrainStateIds = complexDrainStateIds
                    .Distinct()
                    .OrderBy(id => id)
                    .ToArray();
                return token;
            })
            .ToList();

        dbContext.ExecutionTokens.AddRange(tokens);
        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var token in tokens)
        {
            var nodeExecution = NewNodeExecution(
                instance,
                token,
                gateway,
                NodeExecutionKinds.Node,
                NodeExecutionStatuses.Active,
                parentBranchId,
                null,
                triggeredBy,
                now);
            dbContext.NodeExecutions.Add(nodeExecution);
            token.CurrentNodeExecution = nodeExecution;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return tokens.Select(ToRecord).ToList();
    }

    public async Task UpdateExecutionTokenAsync(
        long tokenId,
        CurrentNodeSnapshot node,
        string tokenStatus,
        long? gatewayBranchId,
        int? arrivedViaFlowId,
        string? terminationReason,
        string? claimedBy,
        NodeExecutionActorRecord triggeredBy,
        NodeExecutionCompletionRecord? currentCompletion,
        CancellationToken cancellationToken,
        bool deferSave = false)
    {
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(entity => entity.Id == tokenId)
            ?? await dbContext.ExecutionTokens.SingleAsync(entity => entity.Id == tokenId, cancellationToken);
        var instance = dbContext.WorkflowInstances.Local.SingleOrDefault(entity => entity.Id == token.InstanceId)
            ?? await dbContext.WorkflowInstances.SingleAsync(entity => entity.Id == token.InstanceId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (token.CurrentNodeExecution is not null || token.CurrentNodeExecutionId is not null)
        {
            var completion = currentCompletion
                ?? new NodeExecutionCompletionRecord(
                    NodeExecutionRecordStatuses.Completed,
                    NodeExecutionCompletionReasons.Normal,
                    null,
                    arrivedViaFlowId,
                    gatewayBranchId,
                    triggeredBy);
            await CompleteCurrentNodeExecutionAsync(token, completion, now, cancellationToken);
        }
        token.NodeId = node.Id;
        token.NodeName = node.Name;
        token.NodeExternalId = node.ExternalId;
        token.NodeType = node.Type;
        token.FaultCode = node.FaultCode;
        token.FaultDescription = node.FaultDescription;
        token.Status = tokenStatus;
        token.GatewayBranchId = gatewayBranchId;
        token.ArrivedViaFlowId = arrivedViaFlowId;
        token.ComplexGatewayStateId = null;
        token.ComplexGatewayCycle = null;
        token.TerminationReason = terminationReason;
        token.UpdatedAt = now;
        instance.UpdatedAt = now;

        UserTaskEntity? task = null;
        if (tokenStatus == ExecutionTokenStatuses.Active
            && node.Type == BpmnFlowNodeTypes.UserTask
            && !node.IsMultiInstance)
        {
            task = NewUserTask(instance, token, node, now, claimedBy);
            dbContext.UserTasks.Add(task);
        }

        token.CurrentNodeExecution = null;
        token.CurrentNodeExecutionId = null;
        if (!node.IsMultiInstance)
        {
            var executionStatus = tokenStatus switch
            {
                ExecutionTokenStatuses.Active => NodeExecutionStatuses.Active,
                ExecutionTokenStatuses.Faulted => NodeExecutionStatuses.Faulted,
                ExecutionTokenStatuses.Cancelled => NodeExecutionStatuses.Cancelled,
                ExecutionTokenStatuses.Merged => NodeExecutionStatuses.Merged,
                _ => NodeExecutionStatuses.Completed
            };
            var targetExecution = NewNodeExecution(
                instance,
                token,
                node,
                NodeExecutionKinds.Node,
                NodeExecutionStatuses.Active,
                gatewayBranchId,
                arrivedViaFlowId,
                triggeredBy,
                now,
                task);
            if (executionStatus != NodeExecutionStatuses.Active)
            {
                CompleteNodeExecution(
                    targetExecution,
                    new NodeExecutionCompletionRecord(
                        executionStatus,
                        terminationReason switch
                        {
                            ExecutionTokenTerminationReasons.TerminateEnd =>
                                NodeExecutionCompletionReasons.TerminateEnd,
                            ExecutionTokenTerminationReasons.ErrorEnd =>
                                NodeExecutionCompletionReasons.ErrorEnd,
                            _ => NodeExecutionCompletionReasons.NormalEnd
                        },
                        null,
                        null,
                        gatewayBranchId,
                        triggeredBy,
                        node.FaultCode,
                        node.FaultDescription),
                    now);
            }
            dbContext.NodeExecutions.Add(targetExecution);
            if (executionStatus == NodeExecutionStatuses.Active)
            {
                token.CurrentNodeExecution = targetExecution;
            }
        }
        if (!deferSave)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task SetExecutionTokenStatusAsync(
        long tokenId,
        string tokenStatus,
        string? terminationReason,
        NodeExecutionCompletionRecord completion,
        CancellationToken cancellationToken)
    {
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(entity => entity.Id == tokenId)
            ?? await dbContext.ExecutionTokens.SingleAsync(entity => entity.Id == tokenId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        token.Status = tokenStatus;
        token.TerminationReason = terminationReason;
        token.ComplexGatewayStateId = null;
        token.ComplexGatewayCycle = null;
        token.UpdatedAt = now;
        await CompleteCurrentNodeExecutionAsync(token, completion, now, cancellationToken);
        var persistedTasks = await dbContext.UserTasks
            .Where(entity => entity.TokenId == tokenId
                             && (entity.Status == UserTaskStatuses.Active
                                 || entity.Status == UserTaskStatuses.Pending))
            .OrderBy(entity => entity.Id)
            .ToListAsync(cancellationToken);
        var tasks = persistedTasks
            .Concat(dbContext.UserTasks.Local.Where(entity =>
                entity.TokenId == tokenId
                && (entity.Status == UserTaskStatuses.Active
                    || entity.Status == UserTaskStatuses.Pending)))
            .Distinct()
            .ToList();
        foreach (var task in tasks)
        {
            if (task.Status is UserTaskStatuses.Active or UserTaskStatuses.Pending)
            {
                if (tokenStatus == ExecutionTokenStatuses.Merged)
                {
                    throw new InvalidOperationException(
                        $"Execution token #{tokenId} cannot merge while it owns open user tasks.");
                }
                CompleteTask(task, true, now);
                await CompleteUserTaskNodeExecutionAsync(
                    task,
                    completion with
                    {
                        Status = NodeExecutionRecordStatuses.Cancelled,
                        SelectedFlowId = null,
                        ExitedViaFlowId = null
                    },
                    now,
                    cancellationToken);
            }
        }
    }

    public async Task SetExecutionTokensStatusAsync(
        IReadOnlyCollection<long> tokenIds,
        string tokenStatus,
        string? terminationReason,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var persisted = await dbContext.ExecutionTokens
            .Where(token => tokenIds.Contains(token.Id)
                            && token.Status == ExecutionTokenStatuses.Active)
            .OrderBy(token => token.Id)
            .ToListAsync(cancellationToken);
        var tokens = persisted
            .Concat(dbContext.ExecutionTokens.Local.Where(token =>
                tokenIds.Contains(token.Id) && token.Status == ExecutionTokenStatuses.Active))
            .Distinct()
            .OrderBy(token => token.Id)
            .ToList();
        var currentExecutionIds = tokens
            .Where(token => token.CurrentNodeExecution is null
                            && token.CurrentNodeExecutionId is not null)
            .Select(token => token.CurrentNodeExecutionId!.Value)
            .Distinct()
            .ToArray();
        if (currentExecutionIds.Length > 0)
        {
            var currentExecutions = await dbContext.NodeExecutions
                .Where(execution => currentExecutionIds.Contains(execution.Id))
                .ToDictionaryAsync(execution => execution.Id, cancellationToken);
            foreach (var token in tokens.Where(token =>
                         token.CurrentNodeExecution is null
                         && token.CurrentNodeExecutionId is not null))
            {
                token.CurrentNodeExecution = currentExecutions.GetValueOrDefault(
                    token.CurrentNodeExecutionId!.Value);
            }
        }
        foreach (var token in tokens)
        {
            if (token.Status != ExecutionTokenStatuses.Active)
            {
                continue;
            }
            token.Status = tokenStatus;
            token.TerminationReason = terminationReason;
            token.ComplexGatewayStateId = null;
            token.ComplexGatewayCycle = null;
            token.UpdatedAt = now;
            await CompleteCurrentNodeExecutionAsync(
                token,
                new NodeExecutionCompletionRecord(
                    NodeExecutionRecordStatuses.Cancelled,
                    completionReason,
                    null,
                    null,
                    token.GatewayBranchId,
                    actor),
                now,
                cancellationToken);
        }
    }

    public async Task MergeExecutionTokensAsync(
        IReadOnlyCollection<long> tokenIds,
        long? exitGatewayBranchId,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var persisted = await dbContext.ExecutionTokens
            .Where(token => tokenIds.Contains(token.Id)
                            && token.Status == ExecutionTokenStatuses.Active)
            .OrderBy(token => token.Id)
            .ToListAsync(cancellationToken);
        var tokens = persisted
            .Concat(dbContext.ExecutionTokens.Local.Where(token =>
                tokenIds.Contains(token.Id) && token.Status == ExecutionTokenStatuses.Active))
            .Distinct()
            .OrderBy(token => token.Id)
            .ToList();
        var currentExecutionIds = tokens
            .Where(token => token.CurrentNodeExecution is null
                            && token.CurrentNodeExecutionId is not null)
            .Select(token => token.CurrentNodeExecutionId!.Value)
            .Distinct()
            .ToArray();
        if (currentExecutionIds.Length > 0)
        {
            var currentExecutions = await dbContext.NodeExecutions
                .Where(execution => currentExecutionIds.Contains(execution.Id))
                .ToDictionaryAsync(execution => execution.Id, cancellationToken);
            foreach (var token in tokens.Where(token =>
                         token.CurrentNodeExecution is null
                         && token.CurrentNodeExecutionId is not null))
            {
                token.CurrentNodeExecution = currentExecutions.GetValueOrDefault(
                    token.CurrentNodeExecutionId!.Value);
            }
        }

        foreach (var token in tokens)
        {
            token.Status = ExecutionTokenStatuses.Merged;
            token.TerminationReason = ExecutionTokenTerminationReasons.GatewayJoinMerged;
            token.ComplexGatewayStateId = null;
            token.ComplexGatewayCycle = null;
            token.UpdatedAt = now;
            await CompleteCurrentNodeExecutionAsync(
                token,
                new NodeExecutionCompletionRecord(
                    NodeExecutionRecordStatuses.Merged,
                    completionReason,
                    null,
                    null,
                    exitGatewayBranchId,
                    actor),
                now,
                cancellationToken);
        }
    }

    public async Task SetInstanceStatusAsync(
        long instanceId,
        string status,
        CancellationToken cancellationToken)
    {
        var instance = dbContext.WorkflowInstances.Local.SingleOrDefault(entity => entity.Id == instanceId)
            ?? await dbContext.WorkflowInstances.SingleAsync(entity => entity.Id == instanceId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        instance.Status = status;
        instance.UpdatedAt = now;
        if (status is WorkflowInstanceStatuses.Faulted or WorkflowInstanceStatuses.Cancelled)
        {
            await CancelActiveGatewayScopesAsync(
                instanceId,
                status == WorkflowInstanceStatuses.Faulted ? "errorEnd" : "instanceCancel",
                now,
                cancellationToken);
        }
        await ReleaseBusinessKeyClaimAsync(instance, status, cancellationToken);
    }

    public async Task<GatewayExecutionRecord> AddGatewayExecutionAsync(
        long instanceId,
        int gatewayNodeId,
        string gatewayType,
        string direction,
        string? phase,
        int? cycle,
        long? parentBranchId,
        IReadOnlyList<int> selectedFlowIds,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var execution = new GatewayExecutionEntity
        {
            InstanceId = instanceId,
            GatewayNodeId = gatewayNodeId,
            GatewayType = gatewayType,
            Direction = direction,
            Phase = phase,
            Cycle = cycle,
            SelectedFlowIds = selectedFlowIds.ToArray(),
            ParentBranchId = parentBranchId,
            Status = GatewayExecutionStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (direction == GatewayExecutionDirections.Split)
        {
            for (var index = 0; index < selectedFlowIds.Count; index++)
            {
                execution.Branches.Add(new GatewayBranchEntity
                {
                    OriginatingFlowId = selectedFlowIds[index],
                    Ordinal = index,
                    Status = GatewayBranchStatuses.Active,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }
        }
        dbContext.GatewayExecutions.Add(execution);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(execution);
    }

    public async Task<IReadOnlyList<GatewayExecutionRecord>> ListGatewayExecutionsAsync(
        long instanceId,
        string? status,
        CancellationToken cancellationToken)
    {
        IQueryable<GatewayExecutionEntity> query =
            status == GatewayExecutionStatuses.Active
                ? dbContext.GatewayExecutions
                : dbContext.GatewayExecutions.AsNoTracking();
        query = query
            .Where(execution => execution.InstanceId == instanceId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(execution => execution.Status == status);
        }
        var entities = await query
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(execution => execution.Id);
        foreach (var tracked in dbContext.GatewayExecutions.Local
                     .Where(execution => execution.InstanceId == instanceId))
        {
            byId[tracked.Id] = tracked;
        }
        return byId.Values
            .Where(execution => string.IsNullOrWhiteSpace(status) || execution.Status == status)
            .OrderBy(execution => execution.Id)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<IReadOnlyList<GatewayExecutionRecord>> ListCurrentGatewayExecutionsAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.GatewayExecutions
            .Where(execution =>
                execution.InstanceId == instanceId
                && execution.Status == GatewayExecutionStatuses.Active)
            .OrderBy(execution => execution.Id)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(execution => execution.Id);
        foreach (var tracked in dbContext.GatewayExecutions.Local.Where(execution =>
                     execution.InstanceId == instanceId))
        {
            byId[tracked.Id] = tracked;
        }
        return byId.Values
            .OrderBy(execution => execution.Id)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<GatewayExecutionRecord?> GetGatewayExecutionAsync(
        long executionId,
        CancellationToken cancellationToken)
    {
        var tracked = dbContext.GatewayExecutions.Local
            .SingleOrDefault(execution => execution.Id == executionId);
        if (tracked is not null)
        {
            return ToRecord(tracked);
        }
        var execution = await dbContext.GatewayExecutions.AsNoTracking()
            .SingleOrDefaultAsync(
                candidate => candidate.Id == executionId,
                cancellationToken);
        return execution is null ? null : ToRecord(execution);
    }

    public async Task<IReadOnlyList<GatewayBranchRecord>> ListGatewayBranchesAsync(
        long executionId,
        CancellationToken cancellationToken)
    {
        var localBranches = dbContext.GatewayBranches.Local
            .Where(branch => branch.ExecutionId == executionId)
            .OrderBy(branch => branch.Ordinal)
            .ThenBy(branch => branch.Id)
            .ToList();
        if (localBranches.Count > 0
            && dbContext.GatewayExecutions.Local.Any(execution => execution.Id == executionId))
        {
            return localBranches.Select(ToRecord).ToList();
        }

        var entities = await dbContext.GatewayBranches.AsNoTracking()
            .Where(branch => branch.ExecutionId == executionId)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(branch => branch.Id);
        foreach (var tracked in dbContext.GatewayBranches.Local
                     .Where(branch => branch.ExecutionId == executionId))
        {
            byId[tracked.Id] = tracked;
        }
        return byId.Values
            .OrderBy(branch => branch.Ordinal)
            .ThenBy(branch => branch.Id)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<IReadOnlyList<GatewayBranchRecord>> ListGatewayBranchesForInstanceAsync(
        long instanceId,
        bool activeOnly,
        CancellationToken cancellationToken)
    {
        IQueryable<GatewayBranchEntity> query = activeOnly
            ? dbContext.GatewayBranches
            : dbContext.GatewayBranches.AsNoTracking();
        query = query
            .Where(branch => branch.Execution != null
                             && branch.Execution.InstanceId == instanceId);
        if (activeOnly)
        {
            query = query.Where(branch =>
                branch.Status == GatewayBranchStatuses.Active
                && branch.Execution!.Status == GatewayExecutionStatuses.Active);
        }
        var entities = await query
            .OrderBy(branch => branch.Id)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(branch => branch.Id);
        foreach (var tracked in dbContext.GatewayBranches.Local.Where(branch =>
                     branch.Execution?.InstanceId == instanceId
                     || dbContext.GatewayExecutions.Local.Any(execution =>
                         execution.Id == branch.ExecutionId && execution.InstanceId == instanceId)))
        {
            byId[tracked.Id] = tracked;
        }
        var localExecutionStatuses = dbContext.GatewayExecutions.Local
            .Where(execution => execution.InstanceId == instanceId)
            .ToDictionary(execution => execution.Id, execution => execution.Status);
        return byId.Values
            .Where(branch =>
                !activeOnly
                || (branch.Status == GatewayBranchStatuses.Active
                    && (!localExecutionStatuses.TryGetValue(branch.ExecutionId, out var executionStatus)
                        || executionStatus == GatewayExecutionStatuses.Active)))
            .OrderBy(branch => branch.Id)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<IReadOnlyList<GatewayBranchRecord>> ListGatewayBranchesForExecutionsAsync(
        IReadOnlyCollection<long> executionIds,
        CancellationToken cancellationToken)
    {
        if (executionIds.Count == 0)
        {
            return [];
        }

        var distinctExecutionIds = executionIds.Distinct().ToArray();
        var entities = await dbContext.GatewayBranches.AsNoTracking()
            .Where(branch => distinctExecutionIds.Contains(branch.ExecutionId))
            .OrderBy(branch => branch.Id)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(branch => branch.Id);
        foreach (var tracked in dbContext.GatewayBranches.Local.Where(branch =>
                     distinctExecutionIds.Contains(branch.ExecutionId)))
        {
            byId[tracked.Id] = tracked;
        }
        return byId.Values
            .OrderBy(branch => branch.Id)
            .Select(ToRecord)
            .ToList();
    }

    public Task SetGatewayExecutionStatusAsync(
        long executionId,
        string status,
        string completionReason,
        int? interruptingNodeId,
        long? interruptingTokenId,
        CancellationToken cancellationToken) =>
        SetGatewayExecutionsStatusAsync(
            [executionId],
            status,
            completionReason,
            interruptingNodeId,
            interruptingTokenId,
            cancellationToken);

    public async Task SetGatewayExecutionsStatusAsync(
        IReadOnlyCollection<long> executionIds,
        string status,
        string completionReason,
        int? interruptingNodeId,
        long? interruptingTokenId,
        CancellationToken cancellationToken)
    {
        if (executionIds.Count == 0)
        {
            return;
        }

        var distinctExecutionIds = executionIds.Distinct().ToArray();
        var localExecutions = dbContext.GatewayExecutions.Local
            .Where(execution => distinctExecutionIds.Contains(execution.Id))
            .ToList();
        var localExecutionIds = localExecutions
            .Select(execution => execution.Id)
            .ToHashSet();
        var missingExecutionIds = distinctExecutionIds
            .Where(id => !localExecutionIds.Contains(id))
            .ToArray();
        var persistedExecutions = missingExecutionIds.Length == 0
            ? []
            : await dbContext.GatewayExecutions
                .Where(execution => missingExecutionIds.Contains(execution.Id))
                .OrderBy(execution => execution.Id)
                .ToListAsync(cancellationToken);
        var executions = persistedExecutions
            .Concat(localExecutions)
            .Distinct()
            .OrderBy(execution => execution.Id)
            .ToList();
        if (executions.Count != distinctExecutionIds.Length)
        {
            throw new InvalidOperationException("One or more gateway executions no longer exist.");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var execution in executions)
        {
            execution.Status = status;
            execution.CompletionReason = completionReason;
            execution.InterruptingNodeId = interruptingNodeId;
            execution.InterruptingTokenId = interruptingTokenId;
            execution.UpdatedAt = now;
            execution.CompletedAt = status == GatewayExecutionStatuses.Active ? null : now;
        }

        var branchStatus = status switch
        {
            GatewayExecutionStatuses.Joined or GatewayExecutionStatuses.Completed =>
                GatewayBranchStatuses.Completed,
            GatewayExecutionStatuses.Cancelled => GatewayBranchStatuses.Cancelled,
            _ => null
        };
        if (branchStatus is not null)
        {
            var splitExecutionIds = executions
                .Where(execution => execution.Direction == GatewayExecutionDirections.Split)
                .Select(execution => execution.Id)
                .ToArray();
            if (splitExecutionIds.Length == 0)
            {
                return;
            }
            var persistedBranches = await dbContext.GatewayBranches
                .Where(branch => splitExecutionIds.Contains(branch.ExecutionId)
                                 && branch.Status == GatewayBranchStatuses.Active)
                .OrderBy(branch => branch.Id)
                .ToListAsync(cancellationToken);
            var activeBranches = persistedBranches
                .Concat(dbContext.GatewayBranches.Local.Where(branch =>
                    splitExecutionIds.Contains(branch.ExecutionId)
                    && branch.Status == GatewayBranchStatuses.Active))
                .Distinct()
                .OrderBy(branch => branch.Id)
                .ToList();
            foreach (var branch in activeBranches)
            {
                // The database predicate cannot see an earlier unsaved status
                // change in this DbContext; preserve branch-specific merged or
                // interrupted states already staged by the engine.
                if (branch.Status != GatewayBranchStatuses.Active)
                {
                    continue;
                }
                branch.Status = branchStatus;
                branch.UpdatedAt = now;
                branch.CompletedAt = now;
            }
        }
    }

    public Task SetGatewayBranchStatusAsync(
        long branchId,
        string status,
        CancellationToken cancellationToken) =>
        SetGatewayBranchesStatusAsync([branchId], status, cancellationToken);

    public async Task SetGatewayBranchesStatusAsync(
        IReadOnlyCollection<long> branchIds,
        string status,
        CancellationToken cancellationToken)
    {
        if (branchIds.Count == 0)
        {
            return;
        }

        var distinctBranchIds = branchIds.Distinct().ToArray();
        var localBranches = dbContext.GatewayBranches.Local
            .Where(branch => distinctBranchIds.Contains(branch.Id))
            .ToList();
        var localBranchIds = localBranches
            .Select(branch => branch.Id)
            .ToHashSet();
        var missingBranchIds = distinctBranchIds
            .Where(id => !localBranchIds.Contains(id))
            .ToArray();
        var persistedBranches = missingBranchIds.Length == 0
            ? []
            : await dbContext.GatewayBranches
                .Where(branch => missingBranchIds.Contains(branch.Id))
                .OrderBy(branch => branch.Id)
                .ToListAsync(cancellationToken);
        var branches = persistedBranches
            .Concat(localBranches)
            .Distinct()
            .OrderBy(branch => branch.Id)
            .ToList();
        if (branches.Count != distinctBranchIds.Length)
        {
            throw new InvalidOperationException("One or more gateway branches no longer exist.");
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var branch in branches)
        {
            branch.Status = status;
            branch.UpdatedAt = now;
            branch.CompletedAt = status == GatewayBranchStatuses.Active ? null : now;
        }
    }

    public async Task<ComplexGatewayStateRecord?> GetComplexGatewayStateAsync(
        long instanceId,
        int gatewayNodeId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var tracked = dbContext.ComplexGatewayStates.Local.SingleOrDefault(state =>
            state.InstanceId == instanceId && state.GatewayNodeId == gatewayNodeId);
        if (tracked is not null)
        {
            return ToRecord(tracked);
        }
        var entity = forUpdate
            ? await dbContext.ComplexGatewayStates
                .FromSqlInterpolated($"""
                    SELECT *
                    FROM flowbit.complex_gateway_states
                    WHERE "InstanceId" = {instanceId} AND "GatewayNodeId" = {gatewayNodeId}
                    FOR UPDATE
                    """)
                .SingleOrDefaultAsync(cancellationToken)
            : await dbContext.ComplexGatewayStates.AsNoTracking()
                .SingleOrDefaultAsync(state =>
                    state.InstanceId == instanceId && state.GatewayNodeId == gatewayNodeId,
                    cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<ComplexGatewayStateRecord>> ListComplexGatewayStatesAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var trackedStates = dbContext.ComplexGatewayStates.Local
            .Where(state => state.InstanceId == instanceId)
            .OrderBy(state => state.GatewayNodeId)
            .ToList();
        // Mutation transactions lock and track every state for the instance in
        // GetInstanceForUpdateAsync, so the local set is complete and avoids a
        // repeated reader during scope reconciliation.
        if (dbContext.Database.CurrentTransaction is not null
            && dbContext.WorkflowInstances.Local.Any(instance => instance.Id == instanceId))
        {
            return trackedStates.Select(ToRecord).ToList();
        }
        var entities = await dbContext.ComplexGatewayStates.AsNoTracking()
            .Where(state => state.InstanceId == instanceId)
            .OrderBy(state => state.GatewayNodeId)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(state => state.Id);
        foreach (var tracked in trackedStates)
        {
            byId[tracked.Id] = tracked;
        }
        return byId.Values
            .OrderBy(state => state.GatewayNodeId)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<ComplexGatewayStateRecord> SaveComplexGatewayStateAsync(
        long instanceId,
        int gatewayNodeId,
        string phase,
        int cycle,
        IReadOnlyCollection<int> contributingFlowIds,
        IReadOnlyCollection<int> remainingFlowIds,
        IReadOnlyCollection<long> activationDrainStateIds,
        IReadOnlyCollection<long> drainingTokenIds,
        long? activeExecutionId,
        CancellationToken cancellationToken)
    {
        var state = dbContext.ComplexGatewayStates.Local.SingleOrDefault(candidate =>
                        candidate.InstanceId == instanceId && candidate.GatewayNodeId == gatewayNodeId)
                    ?? await dbContext.ComplexGatewayStates.SingleOrDefaultAsync(candidate =>
                        candidate.InstanceId == instanceId && candidate.GatewayNodeId == gatewayNodeId,
                        cancellationToken);
        var now = DateTimeOffset.UtcNow;
        if (state is null)
        {
            state = new ComplexGatewayStateEntity
            {
                InstanceId = instanceId,
                GatewayNodeId = gatewayNodeId,
                CreatedAt = now
            };
            dbContext.ComplexGatewayStates.Add(state);
        }
        state.Phase = phase;
        state.Cycle = cycle;
        state.ContributingFlowIds = contributingFlowIds.OrderBy(id => id).ToArray();
        state.RemainingFlowIds = remainingFlowIds.OrderBy(id => id).ToArray();
        state.ActivationDrainStateIds = activationDrainStateIds.OrderBy(id => id).ToArray();
        state.DrainingTokenIds = drainingTokenIds.OrderBy(id => id).ToArray();
        state.ActiveExecutionId = activeExecutionId;
        state.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToRecord(state);
    }

    public async Task RegisterTokenAtComplexGatewayAsync(
        long tokenId,
        long? complexGatewayStateId,
        int? complexGatewayCycle,
        CancellationToken cancellationToken)
    {
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(candidate => candidate.Id == tokenId)
                    ?? await dbContext.ExecutionTokens.SingleAsync(
                        candidate => candidate.Id == tokenId,
                        cancellationToken);
        token.ComplexGatewayStateId = complexGatewayStateId;
        token.ComplexGatewayCycle = complexGatewayCycle;
        token.UpdatedAt = DateTimeOffset.UtcNow;
    }

    public async Task AddComplexDrainMarkerAsync(
        IReadOnlyCollection<long> tokenIds,
        long complexGatewayStateId,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0)
        {
            return;
        }
        var persisted = await dbContext.ExecutionTokens
            .Where(token => tokenIds.Contains(token.Id)
                            && token.Status == ExecutionTokenStatuses.Active)
            .OrderBy(token => token.Id)
            .ToListAsync(cancellationToken);
        var tokens = persisted
            .Concat(dbContext.ExecutionTokens.Local.Where(token =>
                tokenIds.Contains(token.Id) && token.Status == ExecutionTokenStatuses.Active))
            .Distinct()
            .ToList();
        foreach (var token in tokens)
        {
            token.ComplexDrainStateIds = token.ComplexDrainStateIds
                .Append(complexGatewayStateId)
                .Distinct()
                .OrderBy(id => id)
                .ToArray();
        }
    }

    public async Task SetComplexDrainMarkersAsync(
        long tokenId,
        IReadOnlyCollection<long> complexGatewayStateIds,
        CancellationToken cancellationToken)
    {
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(entity => entity.Id == tokenId)
                    ?? await dbContext.ExecutionTokens.SingleAsync(
                        entity => entity.Id == tokenId,
                        cancellationToken);
        token.ComplexDrainStateIds = complexGatewayStateIds
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
    }

    public async Task ClearComplexDrainMarkerAsync(
        long instanceId,
        long complexGatewayStateId,
        CancellationToken cancellationToken)
    {
        var persisted = await dbContext.ExecutionTokens
            .Where(token => token.InstanceId == instanceId
                            && token.Status == ExecutionTokenStatuses.Active
                            && token.ComplexDrainStateIds.Contains(complexGatewayStateId))
            .OrderBy(token => token.Id)
            .ToListAsync(cancellationToken);
        var tokens = persisted
            .Concat(dbContext.ExecutionTokens.Local.Where(token =>
                token.InstanceId == instanceId
                && token.Status == ExecutionTokenStatuses.Active
                && token.ComplexDrainStateIds.Contains(complexGatewayStateId)))
            .Distinct()
            .ToList();
        foreach (var token in tokens)
        {
            token.ComplexDrainStateIds = token.ComplexDrainStateIds
                .Where(id => id != complexGatewayStateId)
                .ToArray();
        }
    }

    public async Task CancelOpenUserTasksForTokensAsync(
        IReadOnlyCollection<long> tokenIds,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var persistedTasks = await dbContext.UserTasks
            .Include(task => task.Token)
            .Include(task => task.NodeExecution)
            .Where(task => tokenIds.Contains(task.TokenId)
                           && (task.Status == UserTaskStatuses.Active || task.Status == UserTaskStatuses.Pending))
            .ToListAsync(cancellationToken);
        var tasks = persistedTasks
            .Concat(dbContext.UserTasks.Local.Where(task =>
                tokenIds.Contains(task.TokenId)
                && (task.Status == UserTaskStatuses.Active
                    || task.Status == UserTaskStatuses.Pending)))
            .Distinct()
            .ToList();
        foreach (var task in tasks)
        {
            if (task.Status is UserTaskStatuses.Active or UserTaskStatuses.Pending)
            {
                CompleteTask(task, true, now);
                await CompleteUserTaskNodeExecutionAsync(
                    task,
                    new NodeExecutionCompletionRecord(
                        NodeExecutionRecordStatuses.Cancelled,
                        completionReason,
                        null,
                        null,
                        task.Token?.GatewayBranchId,
                        actor),
                    now,
                    cancellationToken);
            }
        }
    }

    public async Task CancelActiveMultiInstancesForTokensAsync(
        IReadOnlyCollection<long> tokenIds,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken)
    {
        if (tokenIds.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        var executions = await dbContext.MultiInstanceExecutions
            .Where(execution => tokenIds.Contains(execution.TokenId)
                                && execution.Status == MultiInstanceExecutionStatuses.Active)
            .ToListAsync(cancellationToken);
        foreach (var execution in executions)
        {
            if (execution.Status != MultiInstanceExecutionStatuses.Active)
            {
                continue;
            }
            execution.Status = MultiInstanceExecutionStatuses.Cancelled;
            execution.CancelledCount = execution.TotalCount - execution.CompletedCount;
            execution.CompletionReason = completionReason switch
            {
                NodeExecutionCompletionReasons.InstanceCancelled => "instanceCancel",
                NodeExecutionCompletionReasons.GatewayScopeCancelled =>
                    ExecutionTokenTerminationReasons.GatewayScopeCancelled,
                NodeExecutionCompletionReasons.TerminateEnd =>
                    ExecutionTokenTerminationReasons.TerminateEnd,
                NodeExecutionCompletionReasons.ErrorEnd =>
                    ExecutionTokenTerminationReasons.ErrorEnd,
                _ => completionReason
            };
            execution.UpdatedAt = now;
            execution.CompletedAt = now;
        }
    }

    private async Task CancelActiveGatewayScopesAsync(
        long instanceId,
        string completionReason,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var executions = await dbContext.GatewayExecutions
            .Where(execution => execution.InstanceId == instanceId
                                && execution.Status == GatewayExecutionStatuses.Active)
            .OrderBy(execution => execution.Id)
            .ToListAsync(cancellationToken);
        executions = executions
            .Where(execution => execution.Status == GatewayExecutionStatuses.Active)
            .ToList();
        if (executions.Count == 0)
        {
            return;
        }

        var executionIds = executions.Select(execution => execution.Id).ToArray();
        var activeBranches = await dbContext.GatewayBranches
            .Where(branch => executionIds.Contains(branch.ExecutionId)
                             && branch.Status == GatewayBranchStatuses.Active)
            .OrderBy(branch => branch.Id)
            .ToListAsync(cancellationToken);
        foreach (var branch in activeBranches)
        {
            if (branch.Status != GatewayBranchStatuses.Active)
            {
                continue;
            }
            branch.Status = GatewayBranchStatuses.Cancelled;
            branch.UpdatedAt = now;
            branch.CompletedAt = now;
        }

        foreach (var execution in executions)
        {
            if (execution.Status != GatewayExecutionStatuses.Active)
            {
                continue;
            }
            execution.Status = GatewayExecutionStatuses.Cancelled;
            execution.CompletionReason = completionReason;
            execution.InterruptingNodeId = null;
            execution.InterruptingTokenId = null;
            execution.UpdatedAt = now;
            execution.CompletedAt = now;
        }
    }

    private static InboxListItem ToInboxListItem(InboxPageRow row)
    {
        MultiInstanceProgressRecord? progress = null;
        if (row.MultiInstanceExecutionId is not null)
        {
            if (row.MiId is null
                || row.MiInstanceId is null
                || row.MiTokenId is null
                || row.MiNodeId is null
                || row.MiMode is null
                || row.MiSource is null
                || row.MiOnePerActor is null
                || row.MiResultVariable is null
                || row.MiStatus is null
                || row.MiTotalCount is null
                || row.MiCompletedCount is null
                || row.MiCancelledCount is null
                || row.MiCreatedAt is null
                || row.MiUpdatedAt is null)
            {
                throw new InvalidOperationException(
                    $"User task #{row.UserTaskId} references a missing multi-instance execution.");
            }

            var execution = new MultiInstanceExecutionRecord(
                row.MiId.Value,
                row.MiInstanceId.Value,
                row.MiTokenId.Value,
                row.MiNodeId.Value,
                row.MiMode,
                row.MiSource,
                row.MiOnePerActor.Value,
                row.MiResultVariable,
                row.MiStatus,
                row.MiTotalCount.Value,
                row.MiCompletedCount.Value,
                row.MiCancelledCount.Value,
                row.MiWinningFlowId,
                row.MiCompletionReason,
                row.MiCreatedAt.Value,
                row.MiUpdatedAt.Value,
                row.MiCompletedAt);
            progress = new MultiInstanceProgressRecord(
                execution,
                row.MiActiveTaskCount,
                row.MiPendingTaskCount,
                row.MiCancelledTaskCount,
                ParseFlowCounts(row.MiFlowCountsJson));
        }

        return new InboxListItem(
            row.InstanceId,
            row.WorkflowId,
            row.WorkflowDefinitionId,
            row.WorkflowName,
            row.WorkflowVersion,
            row.BusinessKey,
            row.BusinessKeyUniqueness,
            row.TokenId,
            row.UserTaskId,
            row.MultiInstanceExecutionId,
            row.ItemIndex,
            ParseJsonValue(row.ItemValueJson),
            row.Assignee,
            row.CurrentNodeId,
            row.CurrentNodeName,
            row.CurrentNodeExternalId,
            row.CurrentNodeType,
            row.CurrentNodeRoles,
            row.CurrentRequiresClaim,
            row.CurrentRequiresAssignment,
            row.Status,
            row.ClaimedBy,
            row.StartedBy,
            row.TaskCreatedAt,
            row.TaskUpdatedAt,
            row.InstanceCreatedAt,
            row.InstanceUpdatedAt,
            ParseVariables(row.VariablesJson),
            progress)
        {
            ActingFor = row.ActingFor,
            DelegationId = row.DelegationId
        };
    }

    private static JsonElement? ParseJsonValue(string? json)
    {
        if (json is null)
        {
            return null;
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseVariables(string json)
    {
        using var document = JsonDocument.Parse(json);
        var variables = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            variables[property.Name] = property.Value.Clone();
        }

        return variables;
    }

    private static IReadOnlyDictionary<int, int> ParseFlowCounts(string json)
    {
        using var document = JsonDocument.Parse(json);
        var counts = new Dictionary<int, int>();
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (int.TryParse(property.Name, out var flowId))
            {
                counts[flowId] = property.Value.GetInt32();
            }
        }

        return counts;
    }

    // Unmapped EF Core raw-SQL result. JSONB aggregates are projected as text so
    // the persistence boundary owns cloning JsonElement values and no JsonDocument
    // lifetime escapes this repository.
    private sealed class InboxPageRow
    {
        public long InstanceId { get; set; }
        public long WorkflowId { get; set; }
        public long WorkflowDefinitionId { get; set; }
        public string WorkflowName { get; set; } = string.Empty;
        public int WorkflowVersion { get; set; }
        public string? BusinessKey { get; set; }
        public string? BusinessKeyUniqueness { get; set; }
        public long TokenId { get; set; }
        public long UserTaskId { get; set; }
        public long? MultiInstanceExecutionId { get; set; }
        public int? ItemIndex { get; set; }
        public string? ItemValueJson { get; set; }
        public string? Assignee { get; set; }
        public int CurrentNodeId { get; set; }
        public string CurrentNodeName { get; set; } = string.Empty;
        public string? CurrentNodeExternalId { get; set; }
        public string CurrentNodeType { get; set; } = string.Empty;
        public string[] CurrentNodeRoles { get; set; } = [];
        public bool CurrentRequiresClaim { get; set; }
        public bool CurrentRequiresAssignment { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ClaimedBy { get; set; }
        public long? DelegationId { get; set; }
        public string? ActingFor { get; set; }
        public string? StartedBy { get; set; }
        public DateTimeOffset TaskCreatedAt { get; set; }
        public DateTimeOffset TaskUpdatedAt { get; set; }
        public DateTimeOffset InstanceCreatedAt { get; set; }
        public DateTimeOffset InstanceUpdatedAt { get; set; }
        public string VariablesJson { get; set; } = string.Empty;
        public long? MiId { get; set; }
        public long? MiInstanceId { get; set; }
        public long? MiTokenId { get; set; }
        public int? MiNodeId { get; set; }
        public string? MiMode { get; set; }
        public string? MiSource { get; set; }
        public bool? MiOnePerActor { get; set; }
        public string? MiResultVariable { get; set; }
        public string? MiStatus { get; set; }
        public int? MiTotalCount { get; set; }
        public int? MiCompletedCount { get; set; }
        public int? MiCancelledCount { get; set; }
        public int? MiWinningFlowId { get; set; }
        public string? MiCompletionReason { get; set; }
        public DateTimeOffset? MiCreatedAt { get; set; }
        public DateTimeOffset? MiUpdatedAt { get; set; }
        public DateTimeOffset? MiCompletedAt { get; set; }
        public int MiActiveTaskCount { get; set; }
        public int MiPendingTaskCount { get; set; }
        public int MiCancelledTaskCount { get; set; }
        public string MiFlowCountsJson { get; set; } = string.Empty;
    }

    private static UserTaskRecord ToUserTaskRecord(UserTaskPageRow row) =>
        new(row.Id, row.InstanceId, row.TokenId, row.NodeId, row.NodeName,
            row.NodeExternalId, row.Roles, row.RequiresClaim, row.RequiresAssignment,
            row.Status, row.ClaimedBy, row.MultiInstanceExecutionId, row.ItemIndex,
            ParseJsonValue(row.ItemValueJson), row.Assignee, row.SelectedFlowId,
            ParseOptionalDictionary(row.ResultJson), row.CompletedBy, row.CompletedByRoles,
            row.CreatedAt, row.UpdatedAt, row.CompletedAt, row.NodeExecutionId)
        {
            CompletedActingFor = row.CompletedActingFor,
            CompletionDelegationId = row.CompletionDelegationId,
            ActingFor = row.ActingFor,
            DelegationId = row.DelegationId
        };

    private static Dictionary<string, JsonElement>? ParseOptionalDictionary(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);

    private sealed class UserTaskPageRow
    {
        public long Id { get; set; }
        public long InstanceId { get; set; }
        public long TokenId { get; set; }
        public int NodeId { get; set; }
        public string NodeName { get; set; } = string.Empty;
        public string? NodeExternalId { get; set; }
        public string[] Roles { get; set; } = [];
        public bool RequiresClaim { get; set; }
        public bool RequiresAssignment { get; set; }
        public string Status { get; set; } = string.Empty;
        public string? ClaimedBy { get; set; }
        public long? MultiInstanceExecutionId { get; set; }
        public int? ItemIndex { get; set; }
        public string? ItemValueJson { get; set; }
        public string? Assignee { get; set; }
        public int? SelectedFlowId { get; set; }
        public string? ResultJson { get; set; }
        public string? CompletedBy { get; set; }
        public string[]? CompletedByRoles { get; set; }
        public string? CompletedActingFor { get; set; }
        public long? CompletionDelegationId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public long? NodeExecutionId { get; set; }
        public long? DelegationId { get; set; }
        public string? ActingFor { get; set; }
    }

    private async Task<IReadOnlyList<ManagedUserTaskRecord>> ToManagedUserTaskRecordsAsync(
        IReadOnlyList<UserTaskEntity> tasks,
        bool includeVariables,
        CancellationToken cancellationToken)
    {
        if (tasks.Count == 0) return [];
        var instanceIds = tasks.Select(task => task.InstanceId).Distinct().ToList();
        var instances = await dbContext.WorkflowInstances.AsNoTracking()
            .Where(instance => instanceIds.Contains(instance.Id))
            .ToDictionaryAsync(instance => instance.Id, cancellationToken);
        var definitionIds = instances.Values
            .Select(instance => instance.WorkflowDefinitionId)
            .Distinct()
            .ToList();
        var definitions = await dbContext.WorkflowDefinitions.AsNoTracking()
            .Where(definition => definitionIds.Contains(definition.Id))
            .Select(definition => new
            {
                definition.Id,
                definition.WorkflowKey,
                definition.Name,
                definition.Version
            })
            .ToDictionaryAsync(definition => definition.Id, cancellationToken);
        var variablesByInstance = includeVariables
            ? await GetLatestVariableValuesAsync(instanceIds, cancellationToken)
            : null;

        return tasks.Select(task =>
        {
            var instance = instances[task.InstanceId];
            var definition = definitions[instance.WorkflowDefinitionId];
            return new ManagedUserTaskRecord(
                task.Id,
                task.InstanceId,
                task.TokenId,
                definition.Id,
                definition.WorkflowKey,
                definition.Name,
                definition.Version,
                instance.BusinessKey,
                task.NodeId,
                task.NodeName,
                task.NodeExternalId,
                task.Roles,
                task.RequiresClaim,
                task.RequiresAssignment,
                task.ClaimedBy,
                task.Assignee,
                task.MultiInstanceExecutionId,
                task.ItemIndex,
                task.ItemValueJson?.RootElement.Clone(),
                task.CreatedAt,
                task.UpdatedAt,
                variablesByInstance is null
                    ? null
                    : variablesByInstance.GetValueOrDefault(task.InstanceId)
                      ?? new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase));
        }).ToList();
    }

    public async Task<MultiInstanceExecutionRecord> AddMultiInstanceAsync(
        long instanceId,
        long tokenId,
        CurrentNodeSnapshot node,
        MultiInstanceModel configuration,
        IReadOnlyList<System.Text.Json.JsonElement?> items,
        IReadOnlyList<int> outcomeFlowIds,
        NodeExecutionActorRecord triggeredBy,
        CancellationToken cancellationToken)
    {
        var instance = dbContext.WorkflowInstances.Local.SingleOrDefault(i => i.Id == instanceId)
            ?? await dbContext.WorkflowInstances.SingleAsync(i => i.Id == instanceId, cancellationToken);
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(t => t.Id == tokenId)
            ?? await dbContext.ExecutionTokens.SingleAsync(
                t => t.Id == tokenId
                     && t.InstanceId == instanceId
                     && t.Status == ExecutionTokenStatuses.Active,
                cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var execution = new MultiInstanceExecutionEntity
        {
            Instance = instance,
            Token = token,
            NodeId = node.Id,
            Mode = configuration.Mode,
            Source = configuration.Source,
            OnePerActor = configuration.Source == MultiInstanceSources.Cardinality
                          && configuration.OnePerActor,
            ResultVariable = configuration.ResultVariable,
            Status = MultiInstanceExecutionStatuses.Active,
            TotalCount = items.Count,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.MultiInstanceExecutions.Add(execution);
        foreach (var flowId in outcomeFlowIds.Distinct())
        {
            dbContext.MultiInstanceFlowCounts.Add(new MultiInstanceFlowCountEntity
            {
                Execution = execution,
                FlowId = flowId
            });
        }

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var assigned = configuration.Source == MultiInstanceSources.Collection
                ? item?.GetString()?.Trim()
                : null;
            var task = new UserTaskEntity
            {
                Instance = instance,
                Token = token,
                MultiInstanceExecution = execution,
                NodeId = node.Id,
                NodeName = node.Name,
                NodeExternalId = node.ExternalId,
                Roles = node.Roles.ToList(),
                RequiresClaim = configuration.Source == MultiInstanceSources.Cardinality && node.RequiresClaim,
                RequiresAssignment = node.RequiresAssignment,
                Status = configuration.Mode == MultiInstanceModes.Parallel || index == 0
                    ? UserTaskStatuses.Active
                    : UserTaskStatuses.Pending,
                ItemIndex = index,
                ItemValueJson = item is null ? null : JsonMapping.ToJsonDocument(item.Value),
                Assignee = assigned,
                CreatedAt = now,
                UpdatedAt = now
            };
            dbContext.UserTasks.Add(task);
            var executionStatus = configuration.Mode == MultiInstanceModes.Parallel || index == 0
                ? NodeExecutionStatuses.Active
                : NodeExecutionStatuses.Pending;
            dbContext.NodeExecutions.Add(NewNodeExecution(
                instance,
                token,
                node,
                NodeExecutionKinds.UserTaskItem,
                executionStatus,
                token.GatewayBranchId,
                token.ArrivedViaFlowId,
                triggeredBy,
                now,
                task,
                execution,
                index));
        }

        token.CurrentNodeExecution = null;
        token.CurrentNodeExecutionId = null;
        return ToRecord(execution);
    }

    public async Task<MultiInstanceExecutionRecord?> GetActiveMultiInstanceAsync(
        long tokenId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        MultiInstanceExecutionEntity? entity;
        if (forUpdate)
        {
            entity = await dbContext.MultiInstanceExecutions
                .FromSqlInterpolated($"SELECT * FROM flowbit.multi_instance_executions WHERE \"TokenId\" = {tokenId} AND \"Status\" = {MultiInstanceExecutionStatuses.Active} ORDER BY \"Id\" DESC LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            entity = await dbContext.MultiInstanceExecutions.AsNoTracking()
                .Where(e => e.TokenId == tokenId
                            && e.Status == MultiInstanceExecutionStatuses.Active)
                .OrderByDescending(e => e.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<MultiInstanceExecutionRecord>> ListMultiInstancesAsync(
        long instanceId,
        string? status,
        CancellationToken cancellationToken)
    {
        var query = dbContext.MultiInstanceExecutions.AsNoTracking()
            .Where(execution => execution.InstanceId == instanceId);
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(execution => execution.Status == status);
        }
        return (await query.OrderBy(execution => execution.Id).ToListAsync(cancellationToken))
            .Select(ToRecord)
            .ToList();
    }

    public async Task<IReadOnlyList<MultiInstanceExecutionRecord>> ListCurrentMultiInstancesAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.MultiInstanceExecutions.AsNoTracking()
            .Where(execution =>
                execution.InstanceId == instanceId
                && execution.Status == MultiInstanceExecutionStatuses.Active)
            .OrderBy(execution => execution.Id)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(execution => execution.Id);
        foreach (var tracked in dbContext.MultiInstanceExecutions.Local.Where(execution =>
                     execution.InstanceId == instanceId))
        {
            byId[tracked.Id] = tracked;
        }
        return byId.Values
            .OrderBy(execution => execution.Id)
            .Select(ToRecord)
            .ToList();
    }

    public async Task<UserTaskRecord?> GetUserTaskAsync(long taskId, bool forUpdate, CancellationToken cancellationToken)
    {
        UserTaskEntity? entity;
        if (forUpdate)
        {
            entity = await dbContext.UserTasks
                .FromSqlInterpolated($"SELECT * FROM flowbit.user_tasks WHERE \"Id\" = {taskId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            entity = await dbContext.UserTasks.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        }
        if (entity is null)
        {
            return null;
        }
        var nodeExecutionId = dbContext.NodeExecutions.Local
            .SingleOrDefault(execution => execution.UserTaskId == entity.Id)?.Id
            ?? await dbContext.NodeExecutions.AsNoTracking()
                .Where(execution => execution.UserTaskId == entity.Id)
                .Select(execution => (long?)execution.Id)
                .SingleOrDefaultAsync(cancellationToken);
        return ToRecord(entity, nodeExecutionId);
    }

    public async Task<UserTaskRecord?> GetActiveUserTaskAsync(
        long instanceId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UserTaskEntity> entities;
        if (forUpdate)
        {
            entities = await dbContext.UserTasks
                .FromSqlInterpolated($"SELECT * FROM flowbit.user_tasks WHERE \"InstanceId\" = {instanceId} AND \"Status\" = {UserTaskStatuses.Active} ORDER BY \"Id\" FOR UPDATE")
                .ToListAsync(cancellationToken);
        }
        else
        {
            entities = await dbContext.UserTasks.AsNoTracking()
                .Where(t => t.InstanceId == instanceId && t.Status == UserTaskStatuses.Active)
                .OrderBy(t => t.Id)
                .ToListAsync(cancellationToken);
        }

        var activeById = entities.ToDictionary(t => t.Id);
        foreach (var tracked in dbContext.UserTasks.Local.Where(t => t.InstanceId == instanceId))
        {
            if (tracked.Status == UserTaskStatuses.Active)
            {
                activeById[tracked.Id] = tracked;
            }
            else
            {
                activeById.Remove(tracked.Id);
            }
        }

        if (activeById.Count != 1)
        {
            return null;
        }
        var active = activeById.Values.Single();
        var nodeExecutionId = dbContext.NodeExecutions.Local
            .SingleOrDefault(execution => execution.UserTaskId == active.Id)?.Id
            ?? await dbContext.NodeExecutions.AsNoTracking()
                .Where(execution => execution.UserTaskId == active.Id)
                .Select(execution => (long?)execution.Id)
                .SingleOrDefaultAsync(cancellationToken);
        return ToRecord(active, nodeExecutionId);
    }

    public async Task<MultiInstanceExecutionRecord?> GetMultiInstanceAsync(
        long executionId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        MultiInstanceExecutionEntity? entity;
        if (forUpdate)
        {
            entity = await dbContext.MultiInstanceExecutions
                .FromSqlInterpolated($"SELECT * FROM flowbit.multi_instance_executions WHERE \"Id\" = {executionId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            entity = await dbContext.MultiInstanceExecutions.AsNoTracking()
                .SingleOrDefaultAsync(e => e.Id == executionId, cancellationToken);
        }
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<UserTaskRecord>> ListUserTasksAsync(
        long instanceId,
        string? status,
        CancellationToken cancellationToken)
    {
        var query = dbContext.UserTasks.AsNoTracking().Where(t => t.InstanceId == instanceId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(t => t.Status == status);
        return (await query.OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.Id)
                .ToListAsync(cancellationToken))
            .Select(entity => ToRecord(entity)).ToList();
    }

    public async Task<IReadOnlyList<UserTaskRecord>> ListCurrentUserTasksAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.UserTasks.AsNoTracking()
            .Where(task =>
                task.InstanceId == instanceId
                && (task.Status == UserTaskStatuses.Active
                    || task.Status == UserTaskStatuses.Pending))
            .OrderByDescending(task => task.UpdatedAt)
            .ThenByDescending(task => task.Id)
            .ToListAsync(cancellationToken);
        var byId = entities.ToDictionary(task => task.Id);
        foreach (var tracked in dbContext.UserTasks.Local.Where(task =>
                     task.InstanceId == instanceId))
        {
            byId[tracked.Id] = tracked;
        }
        return byId.Values
            .OrderByDescending(task => task.UpdatedAt)
            .ThenByDescending(task => task.Id)
            .Select(task => ToRecord(task))
            .ToList();
    }

    public async Task<PagedResult<UserTaskRecord>> ListUserTasksPageAsync(
        long instanceId,
        string? status,
        string user,
        IReadOnlyCollection<string> roles,
        DateTimeOffset asOf,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var lowerRoles = roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim().ToLowerInvariant())
            .Distinct()
            .ToArray();
        var where = new StringBuilder("""
             WHERE ut."InstanceId" = @instanceId
               AND (
                    ut."Assignee" IS NULL
                    OR lower(ut."Assignee") = lower(@user)
                    OR delegation."Id" IS NOT NULL
               )
               AND (NOT ut."RequiresAssignment" OR ut."Assignee" IS NOT NULL)
               AND (
                    cardinality(ut."Roles") = 0
                    OR EXISTS (
                        SELECT 1 FROM unnest(ut."Roles") AS node_role
                        WHERE lower(node_role) = ANY(@lowerRoles)
                    )
               )
            """);
        var args = new List<(string Name, object Value)>
        {
            ("instanceId", instanceId),
            ("user", user),
            ("delegationAsOf", asOf),
            ("lowerRoles", lowerRoles)
        };
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Append(" AND ut.\"Status\" = @taskStatus");
            args.Add(("taskStatus", status));
        }

#pragma warning disable EF1002
        const string from = """
            FROM flowbit.user_tasks ut
            JOIN flowbit.workflow_instances w ON w."Id" = ut."InstanceId"
            LEFT JOIN LATERAL (
                SELECT d."Id", d."Delegator"
                FROM flowbit.user_delegations d
                WHERE d."Delegate" = @user
                  AND d."Delegator" = COALESCE(ut."Assignee", ut."ClaimedBy")
                  AND d."WorkflowKey" = w."WorkflowKey"
                  AND d."RevokedAt" IS NULL
                  AND d."ValidFrom" <= @delegationAsOf
                  AND @delegationAsOf < d."ValidUntil"
                  AND d."AcceptanceState" IN ('notRequired', 'accepted')
                ORDER BY d."ValidFrom" DESC, d."Id" DESC
                LIMIT 1
            ) delegation ON TRUE
            """;
        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS \"Value\" {from} {where}",
                BuildParameters(args))
            .SingleAsync(cancellationToken);
        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("take", pageSize),
            ("skip", (page - 1) * pageSize)
        };
        var taskRows = await dbContext.Database
            .SqlQueryRaw<UserTaskPageRow>(
                $"""
                SELECT ut."Id" AS "Id",
                       ut."InstanceId" AS "InstanceId",
                       ut."TokenId" AS "TokenId",
                       ut."NodeId" AS "NodeId",
                       ut."NodeName" AS "NodeName",
                       ut."NodeExternalId" AS "NodeExternalId",
                       ut."Roles" AS "Roles",
                       ut."RequiresClaim" AS "RequiresClaim",
                       ut."RequiresAssignment" AS "RequiresAssignment",
                       ut."Status" AS "Status",
                       ut."ClaimedBy" AS "ClaimedBy",
                       ut."MultiInstanceExecutionId" AS "MultiInstanceExecutionId",
                       ut."ItemIndex" AS "ItemIndex",
                       ut."ItemValueJson"::text AS "ItemValueJson",
                       ut."Assignee" AS "Assignee",
                       ut."SelectedFlowId" AS "SelectedFlowId",
                       ut."ResultJson"::text AS "ResultJson",
                       ut."CompletedBy" AS "CompletedBy",
                       ut."CompletedByRoles" AS "CompletedByRoles",
                       ut."CompletedActingFor" AS "CompletedActingFor",
                       ut."CompletionDelegationId" AS "CompletionDelegationId",
                       ut."CreatedAt" AS "CreatedAt",
                       ut."UpdatedAt" AS "UpdatedAt",
                       ut."CompletedAt" AS "CompletedAt",
                       node_execution."Id" AS "NodeExecutionId",
                       delegation."Id" AS "DelegationId",
                       delegation."Delegator" AS "ActingFor"
                {from}
                LEFT JOIN flowbit.node_executions node_execution
                       ON node_execution."UserTaskId" = ut."Id"
                {where}
                ORDER BY ut."UpdatedAt" DESC, ut."Id" DESC
                LIMIT @take OFFSET @skip
                """,
                BuildParameters(pageArgs))
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002

        return new PagedResult<UserTaskRecord>(
            taskRows.Select(ToUserTaskRecord).ToList(), page, pageSize, totalCount);
    }

    public async Task<IReadOnlyList<UserTaskRecord>> ListExecutionTasksAsync(long executionId, CancellationToken cancellationToken) =>
        (await dbContext.UserTasks.AsNoTracking()
            .Where(t => t.MultiInstanceExecutionId == executionId)
            .OrderBy(t => t.ItemIndex)
            .ToListAsync(cancellationToken)).Select(entity => ToRecord(entity)).ToList();

    public async Task<AssignmentInheritanceSourceRecord?> GetAssignmentInheritanceSourceAsync(
        long instanceId,
        int? nodeId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.UserTasks.AsNoTracking()
            .Where(task => task.InstanceId == instanceId
                           && task.Status == UserTaskStatuses.Completed
                           && task.CompletedAt != null);
        if (nodeId is not null)
        {
            query = query.Where(task => task.NodeId == nodeId.Value);
        }

        return await query
            .OrderByDescending(task => task.CompletedAt)
            .ThenByDescending(task => task.Id)
            .Select(task => new AssignmentInheritanceSourceRecord(
                task.Id,
                task.NodeId,
                task.Assignee,
                task.CompletedBy,
                task.CompletedActingFor))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<long, UserTaskWorkSummaryRecord>> GetUserTaskWorkSummariesAsync(
        IReadOnlyCollection<long> instanceIds,
        CancellationToken cancellationToken)
    {
        if (instanceIds.Count == 0)
        {
            return new Dictionary<long, UserTaskWorkSummaryRecord>();
        }

        var ids = instanceIds.Distinct().ToList();
        var aggregates = await dbContext.UserTasks.AsNoTracking()
            .Where(task => ids.Contains(task.InstanceId)
                           && (task.Status == UserTaskStatuses.Active
                               || task.Status == UserTaskStatuses.Pending))
            .GroupBy(task => task.InstanceId)
            .Select(group => new
            {
                InstanceId = group.Key,
                IsMultiInstance = group.Any(task => task.MultiInstanceExecutionId != null),
                ActiveCount = group.Count(task => task.Status == UserTaskStatuses.Active),
                PendingCount = group.Count(task => task.Status == UserTaskStatuses.Pending),
                ClaimedCount = group.Count(task => task.Status == UserTaskStatuses.Active && task.ClaimedBy != null),
                AssignedCount = group.Count(task => task.Status == UserTaskStatuses.Active && task.Assignee != null),
                NormalTaskCount = group.Count(task => task.MultiInstanceExecutionId == null),
                MultiInstanceTaskCount = group.Count(task => task.MultiInstanceExecutionId != null)
            })
            .ToListAsync(cancellationToken);

        var soleInstanceIds = aggregates
            .Where(summary => summary.ActiveCount == 1)
            .Select(summary => summary.InstanceId)
            .ToList();
        var soleTasks = soleInstanceIds.Count == 0
            ? new Dictionary<long, (string? ClaimedBy, string? Assignee)>()
            : await dbContext.UserTasks.AsNoTracking()
                .Where(task => soleInstanceIds.Contains(task.InstanceId)
                               && task.Status == UserTaskStatuses.Active)
                .Select(task => new { task.InstanceId, task.ClaimedBy, task.Assignee })
                .ToDictionaryAsync(
                    task => task.InstanceId,
                    task => new ValueTuple<string?, string?>(task.ClaimedBy, task.Assignee),
                    cancellationToken);

        return aggregates.ToDictionary(
            summary => summary.InstanceId,
            summary =>
            {
                soleTasks.TryGetValue(summary.InstanceId, out var sole);
                return new UserTaskWorkSummaryRecord(
                    summary.InstanceId,
                    summary.IsMultiInstance,
                    summary.ActiveCount,
                    summary.PendingCount,
                    summary.ClaimedCount,
                    summary.AssignedCount,
                    sole.Item1,
                    sole.Item2,
                    summary.NormalTaskCount,
                    summary.MultiInstanceTaskCount);
            });
    }

    public async Task<IReadOnlyDictionary<long, MultiInstanceProgressRecord>> GetMultiInstanceProgressAsync(
        IReadOnlyCollection<long> executionIds,
        CancellationToken cancellationToken)
    {
        if (executionIds.Count == 0)
        {
            return new Dictionary<long, MultiInstanceProgressRecord>();
        }

        var ids = executionIds.Distinct().ToList();
        var executions = await dbContext.MultiInstanceExecutions.AsNoTracking()
            .Where(execution => ids.Contains(execution.Id))
            .Select(execution => new
            {
                Execution = execution,
                ActiveCount = execution.UserTasks.Count(task => task.Status == UserTaskStatuses.Active),
                PendingCount = execution.UserTasks.Count(task => task.Status == UserTaskStatuses.Pending),
                CancelledCount = execution.UserTasks.Count(task => task.Status == UserTaskStatuses.Cancelled)
            })
            .ToListAsync(cancellationToken);
        var flowCounts = await dbContext.MultiInstanceFlowCounts.AsNoTracking()
            .Where(count => ids.Contains(count.ExecutionId))
            .OrderBy(count => count.FlowId)
            .ToListAsync(cancellationToken);
        var countsByExecution = flowCounts
            .GroupBy(count => count.ExecutionId)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyDictionary<int, int>)group.ToDictionary(
                    count => count.FlowId,
                    count => count.CompletedCount));

        return executions.ToDictionary(
            item => item.Execution.Id,
            item => new MultiInstanceProgressRecord(
                ToRecord(item.Execution),
                item.ActiveCount,
                item.PendingCount,
                item.CancelledCount,
                countsByExecution.GetValueOrDefault(item.Execution.Id)
                    ?? new Dictionary<int, int>()));
    }

    public async Task<IReadOnlyDictionary<long, MultiInstanceActorStateRecord>> GetMultiInstanceActorStatesAsync(
        IReadOnlyCollection<long> executionIds,
        string actor,
        CancellationToken cancellationToken)
    {
        if (executionIds.Count == 0)
        {
            return new Dictionary<long, MultiInstanceActorStateRecord>();
        }

        var ids = executionIds.Distinct().ToArray();
        var normalizedActor = actor.ToLowerInvariant();
        var rows = await dbContext.UserTasks.AsNoTracking()
            .Where(task => task.MultiInstanceExecutionId != null
                           && ids.Contains(task.MultiInstanceExecutionId.Value)
                           && ((task.Status == UserTaskStatuses.Completed
                                && task.CompletedBy != null
                                && (task.CompletedActingFor ?? task.CompletedBy).ToLower() == normalizedActor)
                               || (task.Status == UserTaskStatuses.Active
                                   && ((task.Assignee != null && task.Assignee.ToLower() == normalizedActor)
                                       || (task.ClaimedBy != null && task.ClaimedBy.ToLower() == normalizedActor)))))
            .Select(task => new
            {
                ExecutionId = task.MultiInstanceExecutionId!.Value,
                task.Id,
                task.ItemIndex,
                task.Status
            })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ExecutionId)
            .ToDictionary(
                group => group.Key,
                group => new MultiInstanceActorStateRecord(
                    group.Any(row => row.Status == UserTaskStatuses.Completed),
                    group.Where(row => row.Status == UserTaskStatuses.Active)
                        .OrderBy(row => row.ItemIndex)
                        .Select(row => (long?)row.Id)
                        .FirstOrDefault()));
    }

    public Task<bool> HasCompletedMultiInstanceItemAsync(
        long executionId,
        string completedBy,
        CancellationToken cancellationToken)
    {
        var normalizedUser = completedBy.ToLowerInvariant();
        return dbContext.UserTasks.AsNoTracking().AnyAsync(
            task => task.MultiInstanceExecutionId == executionId
                    && task.Status == UserTaskStatuses.Completed
                    && task.CompletedBy != null
                    && (task.CompletedActingFor ?? task.CompletedBy).ToLower() == normalizedUser,
            cancellationToken);
    }

    public Task<long?> GetClaimedMultiInstanceItemIdAsync(
        long executionId,
        string claimedBy,
        CancellationToken cancellationToken)
    {
        var normalizedUser = claimedBy.ToLowerInvariant();
        return dbContext.UserTasks.AsNoTracking()
            .Where(task => task.MultiInstanceExecutionId == executionId
                           && task.Status == UserTaskStatuses.Active
                           && task.ClaimedBy != null
                           && task.ClaimedBy.ToLower() == normalizedUser)
            .OrderBy(task => task.ItemIndex)
            .Select(task => (long?)task.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<long?> GetOwnedMultiInstanceItemIdAsync(
        long executionId,
        string owner,
        CancellationToken cancellationToken)
    {
        var normalizedUser = owner.ToLowerInvariant();
        return dbContext.UserTasks.AsNoTracking()
            .Where(task => task.MultiInstanceExecutionId == executionId
                           && task.Status == UserTaskStatuses.Active
                           && ((task.Assignee != null && task.Assignee.ToLower() == normalizedUser)
                               || (task.ClaimedBy != null && task.ClaimedBy.ToLower() == normalizedUser)))
            .OrderBy(task => task.ItemIndex)
            .Select(task => (long?)task.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, int>> ListMultiInstanceFlowCountsAsync(
        long executionId,
        CancellationToken cancellationToken) =>
        await dbContext.MultiInstanceFlowCounts
            .Where(c => c.ExecutionId == executionId)
            .ToDictionaryAsync(c => c.FlowId, c => c.CompletedCount, cancellationToken);

    public async Task CompleteMultiInstanceItemAsync(
        long taskId,
        int selectedFlowId,
        string completedBy,
        IReadOnlyList<string> completedByRoles,
        Dictionary<string, System.Text.Json.JsonElement> result,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null)
    {
        var task = dbContext.UserTasks.Local.Single(t => t.Id == taskId);
        var execution = dbContext.MultiInstanceExecutions.Local.Single(e => e.Id == task.MultiInstanceExecutionId);
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(entity => entity.Id == task.TokenId)
            ?? await dbContext.ExecutionTokens.SingleAsync(entity => entity.Id == task.TokenId, cancellationToken);
        var counter = await dbContext.MultiInstanceFlowCounts
            .SingleOrDefaultAsync(c => c.ExecutionId == execution.Id && c.FlowId == selectedFlowId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        task.Status = UserTaskStatuses.Completed;
        task.SelectedFlowId = selectedFlowId;
        task.ResultJson = JsonMapping.ToJsonDocument(result);
        task.CompletedBy = completedBy;
        task.CompletedByRoles = completedByRoles.ToList();
        task.CompletedActingFor = actingFor;
        task.CompletionDelegationId = delegationId;
        task.CompletedAt = now;
        task.UpdatedAt = now;
        execution.CompletedCount++;
        execution.UpdatedAt = now;
        if (counter is not null) counter.CompletedCount++;
        await CompleteUserTaskNodeExecutionAsync(
            task,
            new NodeExecutionCompletionRecord(
                NodeExecutionRecordStatuses.Completed,
                NodeExecutionCompletionReasons.MultiInstanceItem,
                selectedFlowId,
                null,
                token.GatewayBranchId,
                new NodeExecutionActorRecord(completedBy, completedByRoles)
                {
                    ActingFor = actingFor,
                    DelegationId = delegationId
                }),
            now,
            cancellationToken);
    }

    public async Task CompleteUserTaskAsync(
        long taskId,
        int selectedFlowId,
        string completedBy,
        IReadOnlyList<string> completedByRoles,
        Dictionary<string, System.Text.Json.JsonElement> result,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null)
    {
        var task = dbContext.UserTasks.Local.SingleOrDefault(entity => entity.Id == taskId)
            ?? await dbContext.UserTasks.SingleAsync(entity => entity.Id == taskId, cancellationToken);
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(entity => entity.Id == task.TokenId)
            ?? await dbContext.ExecutionTokens.SingleAsync(entity => entity.Id == task.TokenId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        task.Status = UserTaskStatuses.Completed;
        task.SelectedFlowId = selectedFlowId;
        task.ResultJson = JsonMapping.ToJsonDocument(result);
        task.CompletedBy = completedBy;
        task.CompletedByRoles = completedByRoles.ToList();
        task.CompletedActingFor = actingFor;
        task.CompletionDelegationId = delegationId;
        task.CompletedAt = now;
        task.UpdatedAt = now;
        await CompleteUserTaskNodeExecutionAsync(
            task,
            new NodeExecutionCompletionRecord(
                NodeExecutionRecordStatuses.Completed,
                NodeExecutionCompletionReasons.UserAction,
                selectedFlowId,
                selectedFlowId,
                token.GatewayBranchId,
                new NodeExecutionActorRecord(completedBy, completedByRoles)
                {
                    ActingFor = actingFor,
                    DelegationId = delegationId
                }),
            now,
            cancellationToken);
    }

    public async Task ActivateNextMultiInstanceItemAsync(
        long executionId,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken)
    {
        var task = await dbContext.UserTasks
            .Where(t => t.MultiInstanceExecutionId == executionId && t.Status == UserTaskStatuses.Pending)
            .OrderBy(t => t.ItemIndex)
            .FirstOrDefaultAsync(cancellationToken);
        if (task is not null)
        {
            var now = DateTimeOffset.UtcNow;
            task.Status = UserTaskStatuses.Active;
            task.UpdatedAt = now;
            var nodeExecution = dbContext.NodeExecutions.Local
                .SingleOrDefault(entity => entity.UserTaskId == task.Id)
                ?? await dbContext.NodeExecutions.SingleAsync(
                    entity => entity.UserTaskId == task.Id,
                    cancellationToken);
            if (nodeExecution.Status != NodeExecutionStatuses.Pending)
            {
                throw new InvalidOperationException(
                    $"Node execution #{nodeExecution.Id} for user task #{task.Id} is not pending.");
            }
            nodeExecution.Status = NodeExecutionStatuses.Active;
            nodeExecution.StartedAt = now;
            nodeExecution.UpdatedAt = now;
            nodeExecution.TriggeredBy = actor.User;
            nodeExecution.TriggeredByRolesJson = JsonMapping.ToJsonDocument(actor.Roles);
            nodeExecution.TriggeredActingFor = actor.ActingFor;
            nodeExecution.TriggeredDelegationId = actor.DelegationId;
        }
    }

    public async Task CloseMultiInstanceAsync(
        long executionId,
        int winningFlowId,
        string completionReason,
        NodeExecutionActorRecord actor,
        CancellationToken cancellationToken)
    {
        var execution = dbContext.MultiInstanceExecutions.Local.Single(e => e.Id == executionId);
        var remainingCandidates = await dbContext.UserTasks
            .FromSqlInterpolated($"SELECT * FROM flowbit.user_tasks WHERE \"MultiInstanceExecutionId\" = {executionId} AND \"Status\" IN ({UserTaskStatuses.Active}, {UserTaskStatuses.Pending}) ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(cancellationToken);
        // The current item can already be marked completed in the change tracker
        // while the database still reports it active until SaveChanges. Re-check
        // tracked state in memory so the winning item is never cancelled.
        var remaining = remainingCandidates
            .Where(t => t.Status is UserTaskStatuses.Active or UserTaskStatuses.Pending)
            .ToList();
        var now = DateTimeOffset.UtcNow;
        foreach (var task in remaining)
        {
            task.Status = UserTaskStatuses.Cancelled;
            task.CompletedAt = now;
            task.UpdatedAt = now;
            await CompleteUserTaskNodeExecutionAsync(
                task,
                new NodeExecutionCompletionRecord(
                    NodeExecutionRecordStatuses.Cancelled,
                    ToNodeExecutionCompletionReason(completionReason),
                    null,
                    null,
                    null,
                    actor),
                now,
                cancellationToken);
        }
        execution.CancelledCount += remaining.Count;
        execution.WinningFlowId = winningFlowId;
        execution.CompletionReason = completionReason;
        execution.Status = completionReason == "interrupt"
            ? MultiInstanceExecutionStatuses.Interrupted
            : MultiInstanceExecutionStatuses.Completed;
        execution.CompletedAt = now;
        execution.UpdatedAt = now;
    }

    private static string ToNodeExecutionCompletionReason(string completionReason) =>
        completionReason switch
        {
            "condition" or "all" => NodeExecutionCompletionReasons.MultiInstanceCompleted,
            "interrupt" => NodeExecutionCompletionReasons.MultiInstanceInterrupt,
            "instanceCancel" => NodeExecutionCompletionReasons.InstanceCancelled,
            NodeExecutionCompletionReasons.InstanceCancelled => NodeExecutionCompletionReasons.InstanceCancelled,
            NodeExecutionCompletionReasons.GatewayScopeCancelled =>
                NodeExecutionCompletionReasons.GatewayScopeCancelled,
            NodeExecutionCompletionReasons.TerminateEnd => NodeExecutionCompletionReasons.TerminateEnd,
            NodeExecutionCompletionReasons.ErrorEnd => NodeExecutionCompletionReasons.ErrorEnd,
            _ => throw new InvalidOperationException(
                $"Unknown multi-instance completion reason '{completionReason}'.")
        };

    public async Task<DateTimeOffset> UpdateUserTaskClaimAsync(long taskId, string? claimedBy, CancellationToken cancellationToken)
    {
        var task = dbContext.UserTasks.Local.SingleOrDefault(t => t.Id == taskId)
            ?? await dbContext.UserTasks.SingleAsync(t => t.Id == taskId, cancellationToken);
        var clockValue = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(clockValue.Ticks - clockValue.Ticks % 10, clockValue.Offset);
        task.ClaimedBy = claimedBy;
        task.UpdatedAt = now;
        await TouchUserTaskNodeExecutionAsync(task.Id, now, cancellationToken);
        return now;
    }

    public async Task<DateTimeOffset> UpdateUserTaskAssignmentAsync(
        long taskId,
        string? assignee,
        bool requiresClaim,
        CancellationToken cancellationToken)
    {
        var task = dbContext.UserTasks.Local.SingleOrDefault(entity => entity.Id == taskId)
            ?? await dbContext.UserTasks.SingleAsync(entity => entity.Id == taskId, cancellationToken);
        var clockValue = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(clockValue.Ticks - clockValue.Ticks % 10, clockValue.Offset);
        task.Assignee = assignee;
        task.ClaimedBy = null;
        task.RequiresClaim = requiresClaim;
        task.UpdatedAt = now;
        await TouchUserTaskNodeExecutionAsync(task.Id, now, cancellationToken);
        return now;
    }

    public async Task<DateTimeOffset> TouchInstanceAsync(long id, CancellationToken cancellationToken)
    {
        var instance = dbContext.WorkflowInstances.Local.SingleOrDefault(i => i.Id == id)
            ?? await dbContext.WorkflowInstances.SingleAsync(i => i.Id == id, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        instance.UpdatedAt = now;
        return now;
    }

    public Task AddVariableAsync(
        long instanceId,
        string variableName,
        int? sourceActionId,
        string? setBy,
        System.Text.Json.JsonElement value,
        CancellationToken cancellationToken,
        long? nodeExecutionId = null,
        string? actingFor = null,
        long? delegationId = null)
    {
        dbContext.InstanceVariables.Add(new InstanceVariableEntity
        {
            InstanceId = instanceId,
            NodeExecutionId = nodeExecutionId,
            VariableName = variableName,
            SourceActionId = sourceActionId,
            SetBy = setBy,
            ActingFor = actingFor,
            DelegationId = delegationId,
            ValueJson = JsonMapping.ToJsonDocument(value),
            SetAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<InstanceVariableRecord>> ListVariablesAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.InstanceVariables.AsNoTracking()
            .Where(v => v.InstanceId == instanceId)
            .OrderBy(v => v.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    public Task AddHistoryAsync(
        long instanceId,
        int? actionId,
        int fromStepId,
        int toStepId,
        string? performedBy,
        Dictionary<string, System.Text.Json.JsonElement>? payload,
        string? note,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null)
    {
        dbContext.InstanceHistory.Add(new InstanceHistoryEntity
        {
            InstanceId = instanceId,
            ActionId = actionId,
            FromStepId = fromStepId,
            ToStepId = toStepId,
            PerformedBy = performedBy,
            ActingFor = actingFor,
            DelegationId = delegationId,
            Payload = JsonMapping.ToJsonDocument(payload),
            Note = note,
            PerformedAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public Task AddMultiInstanceHistoryAsync(
        long instanceId,
        long tokenId,
        long? userTaskId,
        long executionId,
        int? itemIndex,
        int actionId,
        int fromStepId,
        int toStepId,
        string? performedBy,
        Dictionary<string, System.Text.Json.JsonElement>? payload,
        string note,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null)
    {
        dbContext.InstanceHistory.Add(new InstanceHistoryEntity
        {
            InstanceId = instanceId,
            TokenId = tokenId,
            UserTaskId = userTaskId,
            MultiInstanceExecutionId = executionId,
            ItemIndex = itemIndex,
            ActionId = actionId,
            FromStepId = fromStepId,
            ToStepId = toStepId,
            PerformedBy = performedBy,
            ActingFor = actingFor,
            DelegationId = delegationId,
            Payload = JsonMapping.ToJsonDocument(payload),
            Note = note,
            PerformedAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public Task AddTokenHistoryAsync(
        long instanceId,
        long tokenId,
        int? actionId,
        int fromStepId,
        int toStepId,
        string? performedBy,
        Dictionary<string, JsonElement>? payload,
        string? note,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null)
    {
        dbContext.InstanceHistory.Add(new InstanceHistoryEntity
        {
            InstanceId = instanceId,
            TokenId = tokenId,
            ActionId = actionId,
            FromStepId = fromStepId,
            ToStepId = toStepId,
            PerformedBy = performedBy,
            ActingFor = actingFor,
            DelegationId = delegationId,
            Payload = JsonMapping.ToJsonDocument(payload),
            Note = note,
            PerformedAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public Task AddUserTaskActionHistoryAsync(
        long instanceId,
        long tokenId,
        long userTaskId,
        int actionId,
        int fromStepId,
        int toStepId,
        string performedBy,
        Dictionary<string, System.Text.Json.JsonElement> payload,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null)
    {
        dbContext.InstanceHistory.Add(new InstanceHistoryEntity
        {
            InstanceId = instanceId,
            TokenId = tokenId,
            UserTaskId = userTaskId,
            ActionId = actionId,
            FromStepId = fromStepId,
            ToStepId = toStepId,
            PerformedBy = performedBy,
            ActingFor = actingFor,
            DelegationId = delegationId,
            Payload = JsonMapping.ToJsonDocument(payload),
            PerformedAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public Task AddUserTaskHistoryAsync(
        long instanceId,
        long tokenId,
        long userTaskId,
        long? multiInstanceExecutionId,
        int? itemIndex,
        int nodeId,
        string performedBy,
        Dictionary<string, System.Text.Json.JsonElement> payload,
        string note,
        CancellationToken cancellationToken,
        string? actingFor = null,
        long? delegationId = null)
    {
        dbContext.InstanceHistory.Add(new InstanceHistoryEntity
        {
            InstanceId = instanceId,
            TokenId = tokenId,
            UserTaskId = userTaskId,
            MultiInstanceExecutionId = multiInstanceExecutionId,
            ItemIndex = itemIndex,
            ActionId = null,
            FromStepId = nodeId,
            ToStepId = nodeId,
            PerformedBy = performedBy,
            ActingFor = actingFor,
            DelegationId = delegationId,
            Payload = JsonMapping.ToJsonDocument(payload),
            Note = note,
            PerformedAt = DateTimeOffset.UtcNow
        });
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<InstanceHistoryRecord>> ListHistoryAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        var entities = await dbContext.InstanceHistory.AsNoTracking()
            .Where(h => h.InstanceId == instanceId)
            .OrderBy(h => h.PerformedAt)
            .ThenBy(h => h.Id)
            .ToListAsync(cancellationToken);
        return entities.Select(ToRecord).ToList();
    }

    public Task<long?> GetLatestNodeEntryHistoryIdAsync(
        long instanceId,
        int nodeId,
        CancellationToken cancellationToken) =>
        dbContext.InstanceHistory.AsNoTracking()
            .Where(history => history.InstanceId == instanceId && history.ToStepId == nodeId)
            .OrderByDescending(history => history.Id)
            .Select(history => (long?)history.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<long?> GetLatestTokenNodeEntryHistoryIdAsync(
        long instanceId,
        long tokenId,
        int nodeId,
        CancellationToken cancellationToken) =>
        dbContext.InstanceHistory.AsNoTracking()
            .Where(history => history.InstanceId == instanceId
                              && history.TokenId == tokenId
                              && history.ToStepId == nodeId)
            .OrderByDescending(history => history.Id)
            .Select(history => (long?)history.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<MessageDeliveryReceiptRecord?> GetMessageDeliveryReceiptAsync(
        long instanceId,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.MessageDeliveryReceipts.AsNoTracking()
            .SingleOrDefaultAsync(
                receipt => receipt.InstanceId == instanceId
                           && receipt.IdempotencyKey == idempotencyKey,
                cancellationToken);
        return entity is null ? null : ToRecord(entity);
    }

    public Task AddMessageDeliveryReceiptAsync(
        MessageDeliveryReceiptRecord receipt,
        CancellationToken cancellationToken)
    {
        dbContext.MessageDeliveryReceipts.Add(new MessageDeliveryReceiptEntity
        {
            InstanceId = receipt.InstanceId,
            IdempotencyKey = receipt.IdempotencyKey,
            WaitHistoryId = receipt.WaitHistoryId,
            SourceNodeId = receipt.SourceNodeId,
            CorrelationHeaderName = receipt.CorrelationHeaderName,
            ProofVersion = receipt.ProofVersion,
            CredentialProofSalt = receipt.CredentialProofSalt.ToArray(),
            CredentialProofHash = receipt.CredentialProofHash.ToArray(),
            EnvelopeProofSalt = receipt.EnvelopeProofSalt.ToArray(),
            EnvelopeProofHash = receipt.EnvelopeProofHash.ToArray(),
            CreatedAt = receipt.CreatedAt
        });
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyDictionary<int, SequenceFlowSummaryRecord>> ListSequenceFlowSummariesAsync(
        long instanceId,
        CancellationToken cancellationToken)
    {
        await dbContext.SequenceFlowSummaries
            .Where(summary => summary.InstanceId == instanceId)
            .ToListAsync(cancellationToken);
        loadedSequenceFlowSummaryInstances.Add(instanceId);

        // Read through Local so summaries staged earlier in the caller's transaction
        // are visible even before SaveChanges assigns their database ids.
        return dbContext.SequenceFlowSummaries.Local
            .Where(summary => summary.InstanceId == instanceId)
            .ToDictionary(summary => summary.SequenceFlowId, ToRecord);
    }

    public async Task<SequenceFlowSummaryRecord> AppendSequenceFlowOccurrenceAsync(
        SequenceFlowOccurrenceWriteRecord occurrence,
        CancellationToken cancellationToken)
    {
        if (!occurrence.IsAction && !occurrence.IsTraversal)
        {
            throw new ArgumentException(
                "A sequence-flow occurrence must be an action, a traversal, or both.",
                nameof(occurrence));
        }

        dbContext.SequenceFlowOccurrences.Add(new SequenceFlowOccurrenceEntity
        {
            InstanceId = occurrence.InstanceId,
            SequenceFlowId = occurrence.SequenceFlowId,
            SourceNodeId = occurrence.SourceNodeId,
            TargetNodeId = occurrence.TargetNodeId,
            TokenId = occurrence.TokenId,
            UserTaskId = occurrence.UserTaskId,
            MultiInstanceExecutionId = occurrence.MultiInstanceExecutionId,
            ItemIndex = occurrence.ItemIndex,
            Kind = occurrence.Kind,
            IsAction = occurrence.IsAction,
            IsTraversal = occurrence.IsTraversal,
            User = occurrence.User,
            UserRoles = occurrence.UserRoles.ToList(),
            ActingFor = occurrence.ActingFor,
            DelegationId = occurrence.DelegationId,
            ValuesJson = JsonMapping.ToJsonDocument(occurrence.Values),
            OccurredAt = occurrence.OccurredAt
        });

        var summary = dbContext.SequenceFlowSummaries.Local.SingleOrDefault(candidate =>
            candidate.InstanceId == occurrence.InstanceId
            && candidate.SequenceFlowId == occurrence.SequenceFlowId);
        if (summary is null && !loadedSequenceFlowSummaryInstances.Contains(occurrence.InstanceId))
        {
            summary = await dbContext.SequenceFlowSummaries.SingleOrDefaultAsync(candidate =>
                candidate.InstanceId == occurrence.InstanceId
                && candidate.SequenceFlowId == occurrence.SequenceFlowId, cancellationToken);
        }
        if (summary is null)
        {
            summary = new SequenceFlowSummaryEntity
            {
                InstanceId = occurrence.InstanceId,
                SequenceFlowId = occurrence.SequenceFlowId
            };
            dbContext.SequenceFlowSummaries.Add(summary);
        }

        if (occurrence.IsAction)
        {
            summary.ActionCount = checked(summary.ActionCount + 1);
            summary.LastActionUser = occurrence.User;
            summary.LastActionUserRoles = occurrence.UserRoles.ToList();
            summary.LastActionActingFor = occurrence.ActingFor;
            summary.LastActionDelegationId = occurrence.DelegationId;
            summary.LastActionOccurredAt = occurrence.OccurredAt;
            summary.LastActionKind = occurrence.Kind;
            summary.LastActionValuesJson = JsonMapping.ToJsonDocument(occurrence.Values);
        }

        if (occurrence.IsTraversal)
        {
            summary.TraversalCount = checked(summary.TraversalCount + 1);
            summary.LastTraversalUser = occurrence.User;
            summary.LastTraversalUserRoles = occurrence.UserRoles.ToList();
            summary.LastTraversalActingFor = occurrence.ActingFor;
            summary.LastTraversalDelegationId = occurrence.DelegationId;
            summary.LastTraversalOccurredAt = occurrence.OccurredAt;
            summary.LastTraversalKind = occurrence.Kind;
            summary.LastTraversalValuesJson = JsonMapping.ToJsonDocument(occurrence.Values);
        }

        return ToRecord(summary);
    }

    public async Task<IdempotencyReservationRecord> ReserveIdempotencyKeyAsync(
        string workflowKey,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO flowbit.workflow_idempotency_claims
                ("WorkflowKey", "IdempotencyKey", "InstanceId", "CreatedAt")
            VALUES ({workflowKey}, {idempotencyKey}, NULL, now())
            ON CONFLICT ("WorkflowKey", "IdempotencyKey") DO NOTHING
            """, cancellationToken);

        var claim = await dbContext.WorkflowIdempotencyClaims
            .FromSqlInterpolated($"SELECT * FROM flowbit.workflow_idempotency_claims WHERE \"WorkflowKey\" = {workflowKey} AND \"IdempotencyKey\" = {idempotencyKey} FOR UPDATE")
            .SingleAsync(cancellationToken);
        if (inserted == 0)
        {
            return new IdempotencyReservationRecord(
                false,
                claim.InstanceId ?? throw new InvalidOperationException("A committed idempotency claim has no instance."));
        }

        return new IdempotencyReservationRecord(true, null);
    }

    public async Task BindIdempotencyKeyAsync(
        string workflowKey,
        string idempotencyKey,
        long instanceId,
        CancellationToken cancellationToken)
    {
        var claim = dbContext.WorkflowIdempotencyClaims.Local.SingleOrDefault(candidate =>
                        candidate.WorkflowKey == workflowKey && candidate.IdempotencyKey == idempotencyKey)
                    ?? await dbContext.WorkflowIdempotencyClaims.SingleAsync(candidate =>
                        candidate.WorkflowKey == workflowKey && candidate.IdempotencyKey == idempotencyKey,
                        cancellationToken);
        claim.InstanceId = instanceId;
    }

    public async Task<BusinessKeyReservationRecord> ReserveBusinessKeyAsync(
        string workflowKey,
        string businessKey,
        string uniqueness,
        CancellationToken cancellationToken)
    {
        var permanent = uniqueness == BusinessKeyUniqueness.All;
        var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO flowbit.workflow_business_key_claims
                ("WorkflowKey", "BusinessKey", "IsPermanent", "ActiveInstanceId", "LastInstanceId")
            VALUES ({workflowKey}, {businessKey}, {permanent}, NULL, NULL)
            ON CONFLICT ("WorkflowKey", "BusinessKey") DO NOTHING
            """, cancellationToken);

        var claim = await dbContext.WorkflowBusinessKeyClaims
            .FromSqlInterpolated($"SELECT * FROM flowbit.workflow_business_key_claims WHERE \"WorkflowKey\" = {workflowKey} AND \"BusinessKey\" = {businessKey} FOR UPDATE")
            .SingleAsync(cancellationToken);

        if (inserted == 0
            && (permanent || claim.IsPermanent || claim.ActiveInstanceId is not null))
        {
            return new BusinessKeyReservationRecord(false, claim.ActiveInstanceId ?? claim.LastInstanceId);
        }

        return new BusinessKeyReservationRecord(true, null);
    }

    public async Task BindBusinessKeyAsync(
        string workflowKey,
        string businessKey,
        long instanceId,
        CancellationToken cancellationToken)
    {
        var claim = dbContext.WorkflowBusinessKeyClaims.Local.SingleOrDefault(c =>
                        c.WorkflowKey == workflowKey && c.BusinessKey == businessKey)
                    ?? await dbContext.WorkflowBusinessKeyClaims.SingleAsync(c =>
                        c.WorkflowKey == workflowKey && c.BusinessKey == businessKey, cancellationToken);
        claim.ActiveInstanceId = instanceId;
        claim.LastInstanceId = instanceId;
    }

    private async Task ReleaseBusinessKeyClaimAsync(
        WorkflowInstanceEntity instance,
        string status,
        CancellationToken cancellationToken)
    {
        if (status == WorkflowInstanceStatuses.Running || instance.BusinessKey is null)
        {
            return;
        }

        var claim = dbContext.WorkflowBusinessKeyClaims.Local.SingleOrDefault(c =>
            c.WorkflowKey == instance.WorkflowKey && c.BusinessKey == instance.BusinessKey);
        claim ??= await dbContext.WorkflowBusinessKeyClaims
            .FromSqlInterpolated($"SELECT * FROM flowbit.workflow_business_key_claims WHERE \"WorkflowKey\" = {instance.WorkflowKey} AND \"BusinessKey\" = {instance.BusinessKey} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (claim?.ActiveInstanceId == instance.Id)
        {
            claim.ActiveInstanceId = null;
        }
    }

    private static WorkflowInstanceRecord ToRecord(
        WorkflowInstanceEntity entity,
        ExecutionTokenEntity token,
        UserTaskEntity? task) =>
        new(
            entity.Id,
            entity.WorkflowDefinitionId,
            entity.WorkflowKey,
            entity.IdempotencyKey,
            entity.BusinessKey,
            entity.BusinessKeyUniqueness,
            token.Id,
            token.NodeId,
            task?.Id,
            entity.Status,
            task?.ClaimedBy,
            entity.StartedBy,
            entity.CreatedAt,
            entity.UpdatedAt,
            token.FaultCode,
            token.FaultDescription,
            token.CurrentNodeExecutionId);

    private static ExecutionTokenEntity? SelectRepresentativeToken(
        string instanceStatus,
        IReadOnlyList<ExecutionTokenEntity> tokens)
    {
        var visible = tokens
            .Where(token => token.Status != ExecutionTokenStatuses.Merged)
            .ToList();
        var fallback = visible
            .OrderByDescending(token => token.UpdatedAt)
            .ThenByDescending(token => token.Id)
            .FirstOrDefault()
            ?? tokens.OrderByDescending(token => token.Id).FirstOrDefault();

        return instanceStatus switch
        {
            WorkflowInstanceStatuses.Running =>
                visible.Where(token => token.Status == ExecutionTokenStatuses.Active)
                    .OrderBy(token => token.Id)
                    .FirstOrDefault()
                ?? fallback,
            WorkflowInstanceStatuses.Faulted =>
                visible.Where(token => token.Status == ExecutionTokenStatuses.Faulted)
                    .OrderByDescending(token => token.UpdatedAt)
                    .ThenByDescending(token => token.Id)
                    .FirstOrDefault()
                ?? fallback,
            WorkflowInstanceStatuses.Completed =>
                visible.Where(token => token.Status == ExecutionTokenStatuses.Completed)
                    .OrderByDescending(token =>
                        token.TerminationReason == ExecutionTokenTerminationReasons.TerminateEnd)
                    .ThenByDescending(token => token.UpdatedAt)
                    .ThenByDescending(token => token.Id)
                    .FirstOrDefault()
                ?? fallback,
            WorkflowInstanceStatuses.Cancelled =>
                visible.Where(token => token.Status == ExecutionTokenStatuses.Cancelled)
                    .OrderByDescending(token => token.UpdatedAt)
                    .ThenByDescending(token => token.Id)
                    .FirstOrDefault()
                ?? fallback,
            _ => fallback
        };
    }

    private static ExecutionTokenRecord ToRecord(ExecutionTokenEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.NodeId,
            entity.NodeName,
            entity.NodeExternalId,
            entity.NodeType,
            entity.FaultCode,
            entity.FaultDescription,
            entity.Status,
            entity.GatewayBranchId,
            entity.ArrivedViaFlowId,
            entity.ComplexGatewayStateId,
            entity.ComplexGatewayCycle,
            entity.ComplexDrainStateIds,
            entity.TerminationReason,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CurrentNodeExecutionId);

    private static GatewayExecutionRecord ToRecord(GatewayExecutionEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.GatewayNodeId,
            entity.GatewayType,
            entity.Direction,
            entity.Phase,
            entity.Cycle,
            entity.SelectedFlowIds,
            entity.ParentBranchId,
            entity.Status,
            entity.CompletionReason,
            entity.InterruptingNodeId,
            entity.InterruptingTokenId,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CompletedAt);

    private static ComplexGatewayStateRecord ToRecord(ComplexGatewayStateEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.GatewayNodeId,
            entity.Phase,
            entity.Cycle,
            entity.ContributingFlowIds,
            entity.RemainingFlowIds,
            entity.ActivationDrainStateIds,
            entity.DrainingTokenIds,
            entity.ActiveExecutionId,
            entity.CreatedAt,
            entity.UpdatedAt);

    private static GatewayBranchRecord ToRecord(GatewayBranchEntity entity) =>
        new(
            entity.Id,
            entity.ExecutionId,
            entity.OriginatingFlowId,
            entity.Ordinal,
            entity.Status,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.CompletedAt);

    private static MultiInstanceExecutionRecord ToRecord(MultiInstanceExecutionEntity entity) =>
        new(entity.Id, entity.InstanceId, entity.TokenId, entity.NodeId, entity.Mode, entity.Source,
            entity.OnePerActor, entity.ResultVariable, entity.Status, entity.TotalCount, entity.CompletedCount,
            entity.CancelledCount, entity.WinningFlowId, entity.CompletionReason, entity.CreatedAt,
            entity.UpdatedAt, entity.CompletedAt);

    private static UserTaskRecord ToRecord(UserTaskEntity entity, long? nodeExecutionId = null) =>
        new UserTaskRecord(entity.Id, entity.InstanceId, entity.TokenId, entity.NodeId, entity.NodeName,
            entity.NodeExternalId, entity.Roles, entity.RequiresClaim, entity.RequiresAssignment, entity.Status,
            entity.ClaimedBy, entity.MultiInstanceExecutionId, entity.ItemIndex,
            entity.ItemValueJson?.RootElement.Clone(), entity.Assignee, entity.SelectedFlowId,
            JsonMapping.ToDictionary(entity.ResultJson), entity.CompletedBy, entity.CompletedByRoles,
            entity.CreatedAt,
            entity.UpdatedAt, entity.CompletedAt, nodeExecutionId ?? entity.NodeExecution?.Id)
        {
            CompletedActingFor = entity.CompletedActingFor,
            CompletionDelegationId = entity.CompletionDelegationId
        };

    private static NodeExecutionRecord ToRecord(NodeExecutionEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.ExecutionTokenId,
            entity.UserTaskId,
            entity.MultiInstanceExecutionId,
            entity.ItemIndex,
            entity.NodeId,
            entity.NodeName,
            entity.NodeExternalId,
            entity.NodeType,
            entity.ExecutionKind,
            entity.Status,
            entity.CompletionReason,
            entity.EntryGatewayBranchId,
            entity.ExitGatewayBranchId,
            entity.EnteredViaFlowId,
            entity.SelectedFlowId,
            entity.ExitedViaFlowId,
            JsonMapping.ToStringList(entity.NodeRolesJson),
            entity.TriggeredBy,
            JsonMapping.ToStringList(entity.TriggeredByRolesJson),
            entity.CompletedBy,
            JsonMapping.ToStringList(entity.CompletedByRolesJson),
            entity.ErrorCode,
            entity.ErrorDescription,
            entity.CreatedAt,
            entity.StartedAt,
            entity.UpdatedAt,
            entity.CompletedAt,
            entity.IsCutoverSeeded)
        {
            TriggeredActingFor = entity.TriggeredActingFor,
            TriggeredDelegationId = entity.TriggeredDelegationId,
            CompletedActingFor = entity.CompletedActingFor,
            CompletedDelegationId = entity.CompletedDelegationId
        };

    private static NodeExecutionEntity NewNodeExecution(
        WorkflowInstanceEntity instance,
        ExecutionTokenEntity token,
        CurrentNodeSnapshot node,
        string executionKind,
        string status,
        long? entryGatewayBranchId,
        int? enteredViaFlowId,
        NodeExecutionActorRecord triggeredBy,
        DateTimeOffset now,
        UserTaskEntity? userTask = null,
        MultiInstanceExecutionEntity? multiInstanceExecution = null,
        int? itemIndex = null) =>
        new()
        {
            Instance = instance,
            ExecutionToken = token,
            UserTask = userTask,
            MultiInstanceExecution = multiInstanceExecution,
            ItemIndex = itemIndex,
            NodeId = node.Id,
            NodeName = node.Name,
            NodeExternalId = node.ExternalId,
            NodeType = node.Type,
            ExecutionKind = executionKind,
            Status = status,
            EntryGatewayBranchId = entryGatewayBranchId,
            EnteredViaFlowId = enteredViaFlowId,
            NodeRolesJson = JsonMapping.ToJsonDocument(node.Roles.ToList()),
            TriggeredBy = triggeredBy.User,
            TriggeredByRolesJson = JsonMapping.ToJsonDocument(triggeredBy.Roles),
            TriggeredActingFor = triggeredBy.ActingFor,
            TriggeredDelegationId = triggeredBy.DelegationId,
            CreatedAt = now,
            StartedAt = status == NodeExecutionStatuses.Pending ? null : now,
            UpdatedAt = now
        };

    private async Task CompleteCurrentNodeExecutionAsync(
        ExecutionTokenEntity token,
        NodeExecutionCompletionRecord completion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nodeExecution = token.CurrentNodeExecution;
        if (nodeExecution is null && token.CurrentNodeExecutionId is long nodeExecutionId)
        {
            nodeExecution = dbContext.NodeExecutions.Local
                .SingleOrDefault(entity => entity.Id == nodeExecutionId)
                ?? await dbContext.NodeExecutions.SingleAsync(
                    entity => entity.Id == nodeExecutionId,
                    cancellationToken);
        }
        if (nodeExecution is null)
        {
            token.CurrentNodeExecutionId = null;
            token.CurrentNodeExecution = null;
            return;
        }

        CompleteNodeExecution(nodeExecution, completion, now);
        token.CurrentNodeExecutionId = null;
        token.CurrentNodeExecution = null;
    }

    private async Task CompleteUserTaskNodeExecutionAsync(
        UserTaskEntity task,
        NodeExecutionCompletionRecord completion,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nodeExecution = task.NodeExecution
            ?? dbContext.NodeExecutions.Local.SingleOrDefault(entity => entity.UserTaskId == task.Id)
            ?? await dbContext.NodeExecutions.SingleOrDefaultAsync(
                entity => entity.UserTaskId == task.Id,
                cancellationToken);
        if (nodeExecution is null)
        {
            return;
        }

        CompleteNodeExecution(nodeExecution, completion, now);
        var token = dbContext.ExecutionTokens.Local.SingleOrDefault(entity => entity.Id == task.TokenId)
            ?? await dbContext.ExecutionTokens.SingleAsync(entity => entity.Id == task.TokenId, cancellationToken);
        if (token.CurrentNodeExecutionId == nodeExecution.Id
            || ReferenceEquals(token.CurrentNodeExecution, nodeExecution))
        {
            token.CurrentNodeExecutionId = null;
            token.CurrentNodeExecution = null;
        }
    }

    private async Task TouchUserTaskNodeExecutionAsync(
        long userTaskId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var nodeExecution = dbContext.NodeExecutions.Local
            .SingleOrDefault(entity => entity.UserTaskId == userTaskId)
            ?? await dbContext.NodeExecutions.SingleOrDefaultAsync(
                entity => entity.UserTaskId == userTaskId,
                cancellationToken);
        if (nodeExecution is not null)
        {
            nodeExecution.UpdatedAt = now;
        }
    }

    private static void CompleteNodeExecution(
        NodeExecutionEntity nodeExecution,
        NodeExecutionCompletionRecord completion,
        DateTimeOffset now)
    {
        if (nodeExecution.Status is not (NodeExecutionStatuses.Pending or NodeExecutionStatuses.Active))
        {
            return;
        }

        nodeExecution.Status = completion.Status;
        nodeExecution.CompletionReason = completion.CompletionReason;
        nodeExecution.SelectedFlowId = completion.SelectedFlowId;
        nodeExecution.ExitedViaFlowId = completion.ExitedViaFlowId;
        nodeExecution.ExitGatewayBranchId = completion.HasExitGatewayBranchSnapshot
            ? completion.ExitGatewayBranchId
            : completion.ExitGatewayBranchId ?? nodeExecution.EntryGatewayBranchId;
        nodeExecution.CompletedBy = completion.Actor.User;
        nodeExecution.CompletedByRolesJson = JsonMapping.ToJsonDocument(completion.Actor.Roles);
        nodeExecution.CompletedActingFor = completion.Actor.ActingFor;
        nodeExecution.CompletedDelegationId = completion.Actor.DelegationId;
        nodeExecution.ErrorCode = completion.ErrorCode;
        nodeExecution.ErrorDescription = LimitNodeExecutionErrorDescription(
            completion.ErrorDescription);
        nodeExecution.CompletedAt = now;
        nodeExecution.UpdatedAt = now;
    }

    private static string? LimitNodeExecutionErrorDescription(string? description)
    {
        if (string.IsNullOrEmpty(description))
        {
            return description;
        }

        var builder = new StringBuilder(
            Math.Min(description.Length, ErrorEndConstraints.MaxDescriptionLength));
        var count = 0;
        foreach (var rune in description.EnumerateRunes())
        {
            if (count == ErrorEndConstraints.MaxDescriptionLength)
            {
                return builder.ToString();
            }

            builder.Append(rune.ToString());
            count++;
        }

        return description;
    }

    private static ExecutionTokenEntity NewToken(
        WorkflowInstanceEntity instance,
        CurrentNodeSnapshot node,
        DateTimeOffset now) =>
        new()
        {
            Instance = instance,
            NodeId = node.Id,
            NodeName = node.Name,
            NodeExternalId = node.ExternalId,
            NodeType = node.Type,
            FaultCode = node.FaultCode,
            FaultDescription = node.FaultDescription,
            Status = ExecutionTokenStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static UserTaskEntity NewUserTask(
        WorkflowInstanceEntity instance,
        ExecutionTokenEntity token,
        CurrentNodeSnapshot node,
        DateTimeOffset now,
        string? claimedBy = null) =>
        new()
        {
            Instance = instance,
            Token = token,
            NodeId = node.Id,
            NodeName = node.Name,
            NodeExternalId = node.ExternalId,
            Roles = node.Roles.ToList(),
            RequiresClaim = node.Assignee is null && node.RequiresClaim,
            RequiresAssignment = node.RequiresAssignment,
            Status = UserTaskStatuses.Active,
            ClaimedBy = claimedBy,
            Assignee = node.Assignee,
            CreatedAt = now,
            UpdatedAt = now
        };

    private static void CompleteTask(UserTaskEntity? task, bool cancelled, DateTimeOffset now)
    {
        if (task is null)
        {
            return;
        }

        task.Status = cancelled ? UserTaskStatuses.Cancelled : UserTaskStatuses.Completed;
        if (cancelled)
        {
            task.SelectedFlowId = null;
            task.ResultJson = null;
            task.CompletedBy = null;
            task.CompletedByRoles = null;
        }
        task.CompletedAt = now;
        task.UpdatedAt = now;
    }

    private static string ToTokenStatus(string instanceStatus) => instanceStatus switch
    {
        WorkflowInstanceStatuses.Running => ExecutionTokenStatuses.Active,
        WorkflowInstanceStatuses.Completed => ExecutionTokenStatuses.Completed,
        WorkflowInstanceStatuses.Faulted => ExecutionTokenStatuses.Faulted,
        WorkflowInstanceStatuses.Cancelled => ExecutionTokenStatuses.Cancelled,
        _ => throw new InvalidOperationException($"Unknown workflow instance status '{instanceStatus}'.")
    };

    private static InstanceVariableRecord ToRecord(InstanceVariableEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.VariableName,
            entity.SourceActionId,
            entity.SetBy,
            entity.ValueJson.RootElement.Clone(),
            entity.SetAt,
            entity.NodeExecutionId,
            entity.ActingFor,
            entity.DelegationId);

    private static InstanceHistoryRecord ToRecord(InstanceHistoryEntity entity) =>
        new(
            entity.Id,
            entity.InstanceId,
            entity.TokenId,
            entity.UserTaskId,
            entity.MultiInstanceExecutionId,
            entity.ItemIndex,
            entity.ActionId,
            entity.FromStepId,
            entity.ToStepId,
            entity.PerformedBy,
            JsonMapping.ToDictionary(entity.Payload),
            entity.Note,
            entity.PerformedAt,
            entity.ActingFor,
            entity.DelegationId);

    private static MessageDeliveryReceiptRecord ToRecord(MessageDeliveryReceiptEntity entity) =>
        new(
            entity.InstanceId,
            entity.IdempotencyKey,
            entity.WaitHistoryId,
            entity.SourceNodeId,
            entity.CorrelationHeaderName,
            entity.ProofVersion,
            entity.CredentialProofSalt.ToArray(),
            entity.CredentialProofHash.ToArray(),
            entity.EnvelopeProofSalt.ToArray(),
            entity.EnvelopeProofHash.ToArray(),
            entity.CreatedAt);

    private static SequenceFlowSummaryRecord ToRecord(SequenceFlowSummaryEntity entity) =>
        new(
            entity.InstanceId,
            entity.SequenceFlowId,
            entity.ActionCount,
            ToEvidence(
                entity.LastActionUser,
                entity.LastActionUserRoles,
                entity.LastActionOccurredAt,
                entity.LastActionKind,
                entity.LastActionValuesJson,
                entity.LastActionActingFor,
                entity.LastActionDelegationId),
            entity.TraversalCount,
            ToEvidence(
                entity.LastTraversalUser,
                entity.LastTraversalUserRoles,
                entity.LastTraversalOccurredAt,
                entity.LastTraversalKind,
                entity.LastTraversalValuesJson,
                entity.LastTraversalActingFor,
                entity.LastTraversalDelegationId));

    private static SequenceFlowEvidenceRecord? ToEvidence(
        string? user,
        IReadOnlyList<string> userRoles,
        DateTimeOffset? occurredAt,
        string? kind,
        JsonDocument? valuesJson,
        string? actingFor,
        long? delegationId) =>
        occurredAt is null || kind is null
            ? null
            : new SequenceFlowEvidenceRecord(
                user,
                userRoles.ToList(),
                occurredAt.Value,
                kind,
                JsonMapping.ToDictionary(valuesJson),
                actingFor,
                delegationId);
}
