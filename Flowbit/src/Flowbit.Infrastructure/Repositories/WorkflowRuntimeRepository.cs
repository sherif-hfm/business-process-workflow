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

        // The start node is pass-through and the transaction is not externally
        // visible yet. Keep its position in the returned record and create the
        // persisted token at the first resolved node, avoiding an insert followed
        // immediately by an update for every new instance.
        var transientToken = NewToken(entity, node, now);
        return ToRecord(entity, transientToken, null);
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
                $"SELECT COUNT(*) AS \"Value\" FROM workflow_instances w JOIN LATERAL (SELECT * FROM execution_tokens et WHERE et.\"InstanceId\" = w.\"Id\" ORDER BY et.\"Id\" DESC LIMIT 1) t ON TRUE{where}",
                BuildParameters(args))
            .SingleAsync(cancellationToken);

        var skip = (page - 1) * pageSize;
        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("take", pageSize),
            ("skip", skip)
        };
        var entities = await dbContext.WorkflowInstances
            .FromSqlRaw(
                $"SELECT w.* FROM workflow_instances w JOIN LATERAL (SELECT * FROM execution_tokens et WHERE et.\"InstanceId\" = w.\"Id\" ORDER BY et.\"Id\" DESC LIMIT 1) t ON TRUE{where} ORDER BY w.\"UpdatedAt\" DESC, w.\"Id\" DESC LIMIT @take OFFSET @skip",
                BuildParameters(pageArgs))
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var items = await ToListItemsAsync(entities, includeVariables, cancellationToken);
        return new PagedResult<InstanceListItem>(items, page, pageSize, totalCount);
    }

    public async Task<PagedResult<InstanceListItem>> ListInboxAsync(
        string user,
        IReadOnlyCollection<string> roles,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        IReadOnlyList<VariableFilter> variableFilters,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        // Roles are matched case-insensitively (mirrors the in-memory role check),
        // so compare lower-cased node roles against lower-cased actor roles.
        var (where, args) = BuildInboxWhere(user, roles, instanceId, workflowId, workflowKey, businessKey, nodeId, nodeExternalId, variableFilters);
        var eligibleCte = $"""
            WITH eligible AS (
                SELECT ut."Id",
                       ut."InstanceId",
                       ut."MultiInstanceExecutionId",
                       ut."UpdatedAt",
                       ROW_NUMBER() OVER (
                           PARTITION BY CASE
                               WHEN COALESCE(mie."OnePerActor", FALSE) THEN mie."Id"
                               ELSE -ut."Id"
                           END
                           ORDER BY
                               CASE
                                   WHEN COALESCE(mie."OnePerActor", FALSE)
                                        AND lower(ut."Assignee") = lower(@user) THEN 0
                                   WHEN COALESCE(mie."OnePerActor", FALSE)
                                        AND lower(ut."ClaimedBy") = lower(@user) THEN 1
                                   ELSE 2
                               END,
                               ut."UpdatedAt" DESC,
                               ut."Id" DESC
                       ) AS inbox_rank
                FROM user_tasks ut
                JOIN workflow_instances w ON ut."InstanceId" = w."Id"
                JOIN workflow_definitions wd ON w."WorkflowDefinitionId" = wd."Id"
                LEFT JOIN multi_instance_executions mie ON mie."Id" = ut."MultiInstanceExecutionId"
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
            return new PagedResult<InstanceListItem>([], page, pageSize, totalCount);
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
                    SELECT e."Id", e."InstanceId", e."MultiInstanceExecutionId", e."UpdatedAt"
                    FROM eligible e
                    WHERE e.inbox_rank = 1
                    ORDER BY e."UpdatedAt" DESC, e."Id" DESC
                    LIMIT @take OFFSET @skip
                ),
                page_instances AS (
                    SELECT DISTINCT page."InstanceId"
                    FROM page_task_ids page
                ),
                latest_variables AS (
                    SELECT DISTINCT ON (v."InstanceId", v."VariableName")
                           v."InstanceId", v."VariableName", v."ValueJson"
                    FROM instance_variables v
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
                    FROM user_tasks task
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
                    FROM multi_instance_flow_counts flow
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
                       w."Status" AS "Status",
                       ut."ClaimedBy" AS "ClaimedBy",
                       w."StartedBy" AS "StartedBy",
                       ut."CreatedAt" AS "CreatedAt",
                       ut."UpdatedAt" AS "UpdatedAt",
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
                JOIN user_tasks ut ON ut."Id" = page."Id"
                JOIN workflow_instances w ON w."Id" = ut."InstanceId"
                JOIN workflow_definitions wd ON wd."Id" = w."WorkflowDefinitionId"
                JOIN execution_tokens token ON token."Id" = ut."TokenId"
                LEFT JOIN variable_values values ON values."InstanceId" = w."Id"
                LEFT JOIN multi_instance_executions mie
                       ON mie."Id" = ut."MultiInstanceExecutionId"
                LEFT JOIN mi_task_counts task_counts
                       ON task_counts."ExecutionId" = mie."Id"
                LEFT JOIN mi_flow_counts flow_counts
                       ON flow_counts."ExecutionId" = mie."Id"
                ORDER BY ut."UpdatedAt" DESC, ut."Id" DESC
                """,
                BuildParameters(pageArgs))
            .ToListAsync(cancellationToken);

        var items = rows.Select(ToInboxListItem).ToList();
        return new PagedResult<InstanceListItem>(items, page, pageSize, totalCount);
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
        AppendNodeIdFilter(where, args, nodeId);
        AppendNodeExternalIdFilter(where, args, nodeExternalId);
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
            FROM user_tasks ut
            JOIN workflow_instances w ON w."Id" = ut."InstanceId"
            JOIN workflow_definitions d ON d."Id" = w."WorkflowDefinitionId"
            JOIN execution_tokens token ON token."Id" = ut."TokenId"
            LEFT JOIN multi_instance_executions mie ON mie."Id" = ut."MultiInstanceExecutionId"
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
        AppendNodeIdFilter(where, args, nodeId);
        AppendNodeExternalIdFilter(where, args, nodeExternalId);
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
            FROM user_tasks ut
            JOIN workflow_instances w ON w."Id" = ut."InstanceId"
            JOIN execution_tokens token ON token."Id" = ut."TokenId"
            LEFT JOIN multi_instance_executions mie ON mie."Id" = ut."MultiInstanceExecutionId"
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
                  )
              AND (
                    NOT COALESCE(mie."OnePerActor", FALSE)
                 OR NOT EXISTS (
                      SELECT 1
                      FROM user_tasks completed
                      WHERE completed."MultiInstanceExecutionId" = mie."Id"
                        AND completed."Status" = @completedTask
                        AND completed."CompletedBy" IS NOT NULL
                        AND lower(completed."CompletedBy") = lower(@user)
                    )
                  )
            """);

        var args = new List<(string Name, object Value)>
        {
            ("status", WorkflowInstanceStatuses.Running),
            ("activeTask", UserTaskStatuses.Active),
            ("completedTask", UserTaskStatuses.Completed),
            ("user", user),
            ("lowerRoles", lowerRoles)
        };

        AppendInstanceIdFilter(where, args, instanceId);
        AppendWorkflowIdFilter(where, args, workflowId);
        AppendWorkflowKeyFilter(where, args, workflowKey);
        AppendBusinessKeyFilter(where, args, businessKey);
        AppendNodeIdFilter(where, args, nodeId);
        AppendNodeExternalIdFilter(where, args, nodeExternalId);
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
            " AND EXISTS (SELECT 1 FROM workflow_definitions d" +
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
        int? nodeId)
    {
        if (nodeId is null)
        {
            return;
        }

        args.Add(("nodeId", nodeId.Value));
        where.Append(where.ToString().Contains("ut.\"Status\"")
            ? " AND ut.\"NodeId\" = @nodeId"
            : " AND t.\"NodeId\" = @nodeId");
    }

    // Filters on the projected token/task externalId (exact, case-insensitive).
    // The value is parameter-bound, so there is no SQL injection surface.
    private static void AppendNodeExternalIdFilter(
        StringBuilder where,
        List<(string Name, object Value)> args,
        string? nodeExternalId)
    {
        if (string.IsNullOrWhiteSpace(nodeExternalId))
        {
            return;
        }

        args.Add(("nodeExternalId", nodeExternalId.Trim()));
        where.Append(where.ToString().Contains("ut.\"Status\"")
            ? " AND lower(ut.\"NodeExternalId\") = lower(@nodeExternalId)"
            : " AND lower(t.\"NodeExternalId\") = lower(@nodeExternalId)");
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
                $" FROM instance_variables v WHERE v.\"InstanceId\" = w.\"Id\"" +
                $" AND v.\"VariableName\" = @vn{i} ORDER BY v.\"Id\" DESC LIMIT 1) = lower(@vv{i})");
        }
    }

    private static NpgsqlParameter[] BuildParameters(IEnumerable<(string Name, object Value)> args) =>
        args.Select(a => new NpgsqlParameter(a.Name, a.Value)).ToArray();

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
            .OrderByDescending(t => t.Id)
            .ToListAsync(cancellationToken);
        var latestTokens = tokens
            .GroupBy(t => t.InstanceId)
            .ToDictionary(g => g.Key, g => g.First());
        var taskSummaries = await GetUserTaskWorkSummariesAsync(instanceIds, cancellationToken);
        var variablesByInstance = includeVariables
            ? await GetLatestVariableValuesAsync(instanceIds, cancellationToken)
            : null;

        return entities.Select(e =>
        {
            definitions.TryGetValue(e.WorkflowDefinitionId, out var definition);
            if (!latestTokens.TryGetValue(e.Id, out var token))
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
                FROM instance_variables AS v
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

        var token = await dbContext.ExecutionTokens.AsNoTracking()
            .Where(t => t.InstanceId == id)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);
        var activeTasks = await dbContext.UserTasks.AsNoTracking()
            .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .Take(2)
            .ToListAsync(cancellationToken);
        var task = activeTasks.Count == 1 ? activeTasks[0] : null;
        return token is null ? null : ToRecord(entity, token, task);
    }

    public async Task<WorkflowInstanceRecord?> GetInstanceForUpdateAsync(
        long id,
        bool lockActiveUserTask,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.WorkflowInstances
            .FromSqlInterpolated($"SELECT * FROM workflow_instances WHERE \"Id\" = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var token = await dbContext.ExecutionTokens
            .FromSqlInterpolated($"SELECT * FROM execution_tokens WHERE \"InstanceId\" = {id} ORDER BY \"Id\" DESC LIMIT 1 FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        var task = lockActiveUserTask
            ? await dbContext.UserTasks
                .FromSqlInterpolated($"SELECT * FROM user_tasks WHERE \"InstanceId\" = {id} AND \"Status\" = {UserTaskStatuses.Active} ORDER BY \"Id\" DESC LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : null;
        return token is null ? null : ToRecord(entity, token, task);
    }

    private static InstanceListItem ToInboxListItem(InboxPageRow row)
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

        return new InstanceListItem(
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
            row.Status,
            row.ClaimedBy,
            row.StartedBy,
            row.CreatedAt,
            row.UpdatedAt,
            null,
            ParseVariables(row.VariablesJson),
            progress);
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
        public string Status { get; set; } = string.Empty;
        public string? ClaimedBy { get; set; }
        public string? StartedBy { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
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
        CurrentNodeSnapshot node,
        MultiInstanceModel configuration,
        IReadOnlyList<System.Text.Json.JsonElement?> items,
        IReadOnlyList<int> outcomeFlowIds,
        CancellationToken cancellationToken)
    {
        var instance = dbContext.WorkflowInstances.Local.SingleOrDefault(i => i.Id == instanceId)
            ?? await dbContext.WorkflowInstances.SingleAsync(i => i.Id == instanceId, cancellationToken);
        var token = dbContext.ExecutionTokens.Local
            .Where(t => t.InstanceId == instanceId && t.Status == ExecutionTokenStatuses.Active)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault()
            ?? await dbContext.ExecutionTokens
                .Where(t => t.InstanceId == instanceId && t.Status == ExecutionTokenStatuses.Active)
                .OrderByDescending(t => t.Id)
                .FirstAsync(cancellationToken);
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
            dbContext.UserTasks.Add(new UserTaskEntity
            {
                Instance = instance,
                Token = token,
                MultiInstanceExecution = execution,
                NodeId = node.Id,
                NodeName = node.Name,
                NodeExternalId = node.ExternalId,
                Roles = node.Roles.ToList(),
                RequiresClaim = configuration.Source == MultiInstanceSources.Cardinality && node.RequiresClaim,
                Status = configuration.Mode == MultiInstanceModes.Parallel || index == 0
                    ? UserTaskStatuses.Active
                    : UserTaskStatuses.Pending,
                ItemIndex = index,
                ItemValueJson = item is null ? null : JsonMapping.ToJsonDocument(item.Value),
                Assignee = assigned,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        return ToRecord(execution);
    }

    public async Task<MultiInstanceExecutionRecord?> GetActiveMultiInstanceAsync(
        long instanceId,
        int nodeId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        MultiInstanceExecutionEntity? entity;
        if (forUpdate)
        {
            entity = await dbContext.MultiInstanceExecutions
                .FromSqlInterpolated($"SELECT * FROM multi_instance_executions WHERE \"InstanceId\" = {instanceId} AND \"NodeId\" = {nodeId} AND \"Status\" = {MultiInstanceExecutionStatuses.Active} ORDER BY \"Id\" DESC LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            entity = await dbContext.MultiInstanceExecutions.AsNoTracking()
                .Where(e => e.InstanceId == instanceId && e.NodeId == nodeId
                            && e.Status == MultiInstanceExecutionStatuses.Active)
                .OrderByDescending(e => e.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<UserTaskRecord?> GetUserTaskAsync(long taskId, bool forUpdate, CancellationToken cancellationToken)
    {
        UserTaskEntity? entity;
        if (forUpdate)
        {
            entity = await dbContext.UserTasks
                .FromSqlInterpolated($"SELECT * FROM user_tasks WHERE \"Id\" = {taskId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            entity = await dbContext.UserTasks.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken);
        }
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<UserTaskRecord?> GetActiveUserTaskAsync(
        long instanceId,
        bool forUpdate,
        CancellationToken cancellationToken)
    {
        var local = dbContext.UserTasks.Local
            .Where(t => t.InstanceId == instanceId && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        if (local is not null)
        {
            return ToRecord(local);
        }

        UserTaskEntity? entity;
        if (forUpdate)
        {
            entity = await dbContext.UserTasks
                .FromSqlInterpolated($"SELECT * FROM user_tasks WHERE \"InstanceId\" = {instanceId} AND \"Status\" = {UserTaskStatuses.Active} ORDER BY \"Id\" DESC LIMIT 1 FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }
        else
        {
            entity = await dbContext.UserTasks.AsNoTracking()
                .Where(t => t.InstanceId == instanceId && t.Status == UserTaskStatuses.Active)
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        return entity is null ? null : ToRecord(entity);
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
                .FromSqlInterpolated($"SELECT * FROM multi_instance_executions WHERE \"Id\" = {executionId} FOR UPDATE")
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
            .Select(ToRecord).ToList();
    }

    public async Task<PagedResult<UserTaskRecord>> ListUserTasksPageAsync(
        long instanceId,
        string? status,
        string user,
        IReadOnlyCollection<string> roles,
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
               AND (ut."Assignee" IS NULL OR lower(ut."Assignee") = lower(@user))
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
            ("lowerRoles", lowerRoles)
        };
        if (!string.IsNullOrWhiteSpace(status))
        {
            where.Append(" AND ut.\"Status\" = @taskStatus");
            args.Add(("taskStatus", status));
        }

#pragma warning disable EF1002
        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS \"Value\" FROM user_tasks ut {where}",
                BuildParameters(args))
            .SingleAsync(cancellationToken);
        var pageArgs = new List<(string Name, object Value)>(args)
        {
            ("take", pageSize),
            ("skip", (page - 1) * pageSize)
        };
        var tasks = await dbContext.UserTasks
            .FromSqlRaw(
                $"SELECT ut.* FROM user_tasks ut {where} ORDER BY ut.\"UpdatedAt\" DESC, ut.\"Id\" DESC LIMIT @take OFFSET @skip",
                BuildParameters(pageArgs))
            .AsNoTracking()
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002

        return new PagedResult<UserTaskRecord>(
            tasks.Select(ToRecord).ToList(), page, pageSize, totalCount);
    }

    public async Task<IReadOnlyList<UserTaskRecord>> ListExecutionTasksAsync(long executionId, CancellationToken cancellationToken) =>
        (await dbContext.UserTasks.AsNoTracking()
            .Where(t => t.MultiInstanceExecutionId == executionId)
            .OrderBy(t => t.ItemIndex)
            .ToListAsync(cancellationToken)).Select(ToRecord).ToList();

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
                AssignedCount = group.Count(task => task.Status == UserTaskStatuses.Active && task.Assignee != null)
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
                    sole.Item2);
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
                                && task.CompletedBy.ToLower() == normalizedActor)
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
                    && task.CompletedBy.ToLower() == normalizedUser,
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
        CancellationToken cancellationToken)
    {
        var task = dbContext.UserTasks.Local.Single(t => t.Id == taskId);
        var execution = dbContext.MultiInstanceExecutions.Local.Single(e => e.Id == task.MultiInstanceExecutionId);
        var counter = await dbContext.MultiInstanceFlowCounts
            .SingleOrDefaultAsync(c => c.ExecutionId == execution.Id && c.FlowId == selectedFlowId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        task.Status = UserTaskStatuses.Completed;
        task.SelectedFlowId = selectedFlowId;
        task.ResultJson = JsonMapping.ToJsonDocument(result);
        task.CompletedBy = completedBy;
        task.CompletedByRoles = completedByRoles.ToList();
        task.CompletedAt = now;
        task.UpdatedAt = now;
        execution.CompletedCount++;
        execution.UpdatedAt = now;
        if (counter is not null) counter.CompletedCount++;
    }

    public async Task CompleteUserTaskAsync(
        long taskId,
        int selectedFlowId,
        string completedBy,
        Dictionary<string, System.Text.Json.JsonElement> result,
        CancellationToken cancellationToken)
    {
        var task = dbContext.UserTasks.Local.SingleOrDefault(entity => entity.Id == taskId)
            ?? await dbContext.UserTasks.SingleAsync(entity => entity.Id == taskId, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        task.Status = UserTaskStatuses.Completed;
        task.SelectedFlowId = selectedFlowId;
        task.ResultJson = JsonMapping.ToJsonDocument(result);
        task.CompletedBy = completedBy;
        task.CompletedAt = now;
        task.UpdatedAt = now;
    }

    public async Task ActivateNextMultiInstanceItemAsync(long executionId, CancellationToken cancellationToken)
    {
        var task = await dbContext.UserTasks
            .Where(t => t.MultiInstanceExecutionId == executionId && t.Status == UserTaskStatuses.Pending)
            .OrderBy(t => t.ItemIndex)
            .FirstOrDefaultAsync(cancellationToken);
        if (task is not null)
        {
            task.Status = UserTaskStatuses.Active;
            task.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    public async Task CloseMultiInstanceAsync(
        long executionId,
        int winningFlowId,
        string completionReason,
        CancellationToken cancellationToken)
    {
        var execution = dbContext.MultiInstanceExecutions.Local.Single(e => e.Id == executionId);
        var remainingCandidates = await dbContext.UserTasks
            .FromSqlInterpolated($"SELECT * FROM user_tasks WHERE \"MultiInstanceExecutionId\" = {executionId} AND \"Status\" IN ({UserTaskStatuses.Active}, {UserTaskStatuses.Pending}) ORDER BY \"Id\" FOR UPDATE")
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

    public async Task CancelActiveMultiInstanceAsync(long instanceId, CancellationToken cancellationToken)
    {
        var executions = await dbContext.MultiInstanceExecutions
            .FromSqlInterpolated($"SELECT * FROM multi_instance_executions WHERE \"InstanceId\" = {instanceId} AND \"Status\" = {MultiInstanceExecutionStatuses.Active} ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(cancellationToken);
        foreach (var execution in executions)
        {
            await CloseMultiInstanceAsync(execution.Id, 0, "instanceCancel", cancellationToken);
            execution.Status = MultiInstanceExecutionStatuses.Cancelled;
            execution.WinningFlowId = null;
        }
    }

    public async Task CancelOpenUserTasksAsync(long instanceId, CancellationToken cancellationToken)
    {
        var tasks = await dbContext.UserTasks
            .FromSqlInterpolated($"SELECT * FROM user_tasks WHERE \"InstanceId\" = {instanceId} AND \"Status\" IN ({UserTaskStatuses.Active}, {UserTaskStatuses.Pending}) ORDER BY \"Id\" FOR UPDATE")
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var task in tasks.Where(task =>
                     task.Status is UserTaskStatuses.Active or UserTaskStatuses.Pending))
        {
            CompleteTask(task, true, now);
        }
    }

    public async Task<DateTimeOffset> UpdateUserTaskClaimAsync(long taskId, string? claimedBy, CancellationToken cancellationToken)
    {
        var task = dbContext.UserTasks.Local.SingleOrDefault(t => t.Id == taskId)
            ?? await dbContext.UserTasks.SingleAsync(t => t.Id == taskId, cancellationToken);
        var clockValue = DateTimeOffset.UtcNow;
        var now = new DateTimeOffset(clockValue.Ticks - clockValue.Ticks % 10, clockValue.Offset);
        task.ClaimedBy = claimedBy;
        task.UpdatedAt = now;
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

    public async Task UpdateInstanceAsync(
        long id,
        int currentStepId,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var trackedEntity = dbContext.WorkflowInstances.Local.SingleOrDefault(i => i.Id == id);
        var entity = trackedEntity
            ?? await dbContext.WorkflowInstances.SingleAsync(i => i.Id == id, cancellationToken);
        entity.Status = status;
        entity.UpdatedAt = now;
        await ReleaseBusinessKeyClaimAsync(entity, status, cancellationToken);

        var trackedToken = dbContext.ExecutionTokens.Local
            .Where(t => t.InstanceId == id)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        var token = trackedToken;
        if (token is null)
        {
            token = await dbContext.ExecutionTokens
                .Where(t => t.InstanceId == id)
                .OrderByDescending(t => t.Id)
                .FirstAsync(cancellationToken);
        }

        var trackedTask = dbContext.UserTasks.Local
            .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        // AddInstanceAsync and GetInstanceForUpdateAsync both preload the complete
        // active execution state. When the instance is tracked, a missing local task
        // therefore means there is no active user task; querying again would add one
        // database round trip to every automatic pass-through hop.
        var task = trackedTask;
        if (trackedEntity is null && task is null)
        {
            task = await dbContext.UserTasks
                .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (status == WorkflowInstanceStatuses.Running)
        {
            if (token.NodeId != currentStepId)
            {
                throw new InvalidOperationException("UpdateInstanceAsync cannot move an execution token; use UpdateInstanceNodeAsync.");
            }
            if (task is not null)
            {
                task.ClaimedBy = claimedBy;
                task.UpdatedAt = now;
            }
            return;
        }

        token.Status = ToTokenStatus(status);
        token.UpdatedAt = now;
        CompleteTask(task, status == WorkflowInstanceStatuses.Cancelled, now);
    }

    public async Task UpdateInstanceNodeAsync(
        long id,
        CurrentNodeSnapshot node,
        string status,
        string? claimedBy,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var trackedEntity = dbContext.WorkflowInstances.Local.SingleOrDefault(i => i.Id == id);
        var entity = trackedEntity
            ?? await dbContext.WorkflowInstances.SingleAsync(i => i.Id == id, cancellationToken);
        var trackedToken = dbContext.ExecutionTokens.Local
            .Where(t => t.InstanceId == id)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        var token = trackedToken;
        if (token is null && trackedEntity is not null)
        {
            token = NewToken(entity, node, now);
            dbContext.ExecutionTokens.Add(token);
        }
        else if (token is null)
        {
            token = await dbContext.ExecutionTokens
                .Where(t => t.InstanceId == id)
                .OrderByDescending(t => t.Id)
                .FirstAsync(cancellationToken);
        }
        var trackedTask = dbContext.UserTasks.Local
            .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
            .OrderByDescending(t => t.Id)
            .FirstOrDefault();
        var task = trackedTask;
        if (trackedEntity is null && task is null)
        {
            task = await dbContext.UserTasks
                .Where(t => t.InstanceId == id && t.Status == UserTaskStatuses.Active)
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }

        CompleteTask(task, status == WorkflowInstanceStatuses.Cancelled, now);
        token.NodeId = node.Id;
        token.NodeName = node.Name;
        token.NodeExternalId = node.ExternalId;
        token.NodeType = node.Type;
        token.FaultCode = node.FaultCode;
        token.FaultDescription = node.FaultDescription;
        token.Status = ToTokenStatus(status);
        token.UpdatedAt = now;
        entity.Status = status;
        entity.UpdatedAt = now;
        await ReleaseBusinessKeyClaimAsync(entity, status, cancellationToken);

        if (status == WorkflowInstanceStatuses.Running && node.Type == BpmnFlowNodeTypes.UserTask && !node.IsMultiInstance)
        {
            dbContext.UserTasks.Add(NewUserTask(entity, token, node, now, claimedBy));
        }
    }

    public Task AddVariableAsync(
        long instanceId,
        string variableName,
        int? sourceActionId,
        string? setBy,
        System.Text.Json.JsonElement value,
        CancellationToken cancellationToken)
    {
        dbContext.InstanceVariables.Add(new InstanceVariableEntity
        {
            InstanceId = instanceId,
            VariableName = variableName,
            SourceActionId = sourceActionId,
            SetBy = setBy,
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
        CancellationToken cancellationToken)
    {
        dbContext.InstanceHistory.Add(new InstanceHistoryEntity
        {
            InstanceId = instanceId,
            ActionId = actionId,
            FromStepId = fromStepId,
            ToStepId = toStepId,
            PerformedBy = performedBy,
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
        CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
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
        CancellationToken cancellationToken)
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
            summary.LastActionOccurredAt = occurrence.OccurredAt;
            summary.LastActionKind = occurrence.Kind;
            summary.LastActionValuesJson = JsonMapping.ToJsonDocument(occurrence.Values);
        }

        if (occurrence.IsTraversal)
        {
            summary.TraversalCount = checked(summary.TraversalCount + 1);
            summary.LastTraversalUser = occurrence.User;
            summary.LastTraversalUserRoles = occurrence.UserRoles.ToList();
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
            INSERT INTO workflow_idempotency_claims
                ("WorkflowKey", "IdempotencyKey", "InstanceId", "CreatedAt")
            VALUES ({workflowKey}, {idempotencyKey}, NULL, now())
            ON CONFLICT ("WorkflowKey", "IdempotencyKey") DO NOTHING
            """, cancellationToken);

        var claim = await dbContext.WorkflowIdempotencyClaims
            .FromSqlInterpolated($"SELECT * FROM workflow_idempotency_claims WHERE \"WorkflowKey\" = {workflowKey} AND \"IdempotencyKey\" = {idempotencyKey} FOR UPDATE")
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
            INSERT INTO workflow_business_key_claims
                ("WorkflowKey", "BusinessKey", "IsPermanent", "ActiveInstanceId", "LastInstanceId")
            VALUES ({workflowKey}, {businessKey}, {permanent}, NULL, NULL)
            ON CONFLICT ("WorkflowKey", "BusinessKey") DO NOTHING
            """, cancellationToken);

        var claim = await dbContext.WorkflowBusinessKeyClaims
            .FromSqlInterpolated($"SELECT * FROM workflow_business_key_claims WHERE \"WorkflowKey\" = {workflowKey} AND \"BusinessKey\" = {businessKey} FOR UPDATE")
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
            .FromSqlInterpolated($"SELECT * FROM workflow_business_key_claims WHERE \"WorkflowKey\" = {instance.WorkflowKey} AND \"BusinessKey\" = {instance.BusinessKey} FOR UPDATE")
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
            token.FaultDescription);

    private static MultiInstanceExecutionRecord ToRecord(MultiInstanceExecutionEntity entity) =>
        new(entity.Id, entity.InstanceId, entity.TokenId, entity.NodeId, entity.Mode, entity.Source,
            entity.OnePerActor, entity.ResultVariable, entity.Status, entity.TotalCount, entity.CompletedCount,
            entity.CancelledCount, entity.WinningFlowId, entity.CompletionReason, entity.CreatedAt,
            entity.UpdatedAt, entity.CompletedAt);

    private static UserTaskRecord ToRecord(UserTaskEntity entity) =>
        new(entity.Id, entity.InstanceId, entity.TokenId, entity.NodeId, entity.NodeName,
            entity.NodeExternalId, entity.Roles, entity.RequiresClaim, entity.Status,
            entity.ClaimedBy, entity.MultiInstanceExecutionId, entity.ItemIndex,
            entity.ItemValueJson?.RootElement.Clone(), entity.Assignee, entity.SelectedFlowId,
            JsonMapping.ToDictionary(entity.ResultJson), entity.CompletedBy, entity.CompletedByRoles,
            entity.CreatedAt,
            entity.UpdatedAt, entity.CompletedAt);

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
            entity.SetAt);

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
            entity.PerformedAt);

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
                entity.LastActionValuesJson),
            entity.TraversalCount,
            ToEvidence(
                entity.LastTraversalUser,
                entity.LastTraversalUserRoles,
                entity.LastTraversalOccurredAt,
                entity.LastTraversalKind,
                entity.LastTraversalValuesJson));

    private static SequenceFlowEvidenceRecord? ToEvidence(
        string? user,
        IReadOnlyList<string> userRoles,
        DateTimeOffset? occurredAt,
        string? kind,
        JsonDocument? valuesJson) =>
        occurredAt is null || kind is null
            ? null
            : new SequenceFlowEvidenceRecord(
                user,
                userRoles.ToList(),
                occurredAt.Value,
                kind,
                JsonMapping.ToDictionary(valuesJson));
}
