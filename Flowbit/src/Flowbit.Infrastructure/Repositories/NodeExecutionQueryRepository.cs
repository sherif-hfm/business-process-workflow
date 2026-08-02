using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// SQL-backed node-execution search. Visibility is part of the base WHERE
/// clause, so authorization is applied before the authoritative count, order,
/// and page. List projection is performed in one bounded query after the count.
/// </summary>
public sealed class NodeExecutionQueryRepository(AppDbContext dbContext)
    : INodeExecutionQueryRepository
{
    private const string DurationSql =
        """EXTRACT(EPOCH FROM (COALESCE(ne."CompletedAt", @asOf) - ne."StartedAt")) * 1000""";

    private const string FromSql = """
        FROM flowbit.node_executions ne
        JOIN flowbit.workflow_instances w
          ON w."Id" = ne."InstanceId"
        JOIN flowbit.workflow_definitions d
          ON d."Id" = ne."WorkflowDefinitionId"
        LEFT JOIN flowbit.user_tasks ut
          ON ut."Id" = ne."UserTaskId"
        LEFT JOIN flowbit.multi_instance_executions mie
          ON mie."Id" = ne."MultiInstanceExecutionId"
        """;

    private const string ProjectionSql = """
        SELECT ne."Id" AS "Id",
               ne."InstanceId" AS "InstanceId",
               d."Id" AS "WorkflowId",
               d."WorkflowKey" AS "WorkflowKey",
               d."Name" AS "WorkflowName",
               d."Version" AS "WorkflowVersion",
               w."BusinessKey" AS "BusinessKey",
               ne."ExecutionTokenId" AS "TokenId",
               ne."UserTaskId" AS "UserTaskId",
               ne."MultiInstanceExecutionId" AS "MultiInstanceExecutionId",
               ne."ItemIndex" AS "ItemIndex",
               ne."EntryGatewayBranchId" AS "EntryGatewayBranchId",
               ne."ExitGatewayBranchId" AS "ExitGatewayBranchId",
               ne."ExecutionKind" AS "ExecutionKind",
               ne."NodeId" AS "NodeId",
               ne."NodeName" AS "NodeName",
               ne."NodeExternalId" AS "NodeExternalId",
               ne."NodeType" AS "NodeType",
               ne."Status" AS "Status",
               w."Status" AS "InstanceStatus",
               ne."CompletionReason" AS "CompletionReason",
               (ne."MultiInstanceExecutionId" IS NOT NULL) AS "IsMultiInstance",
               COALESCE(ut."Assignee", ut."ClaimedBy") AS "Owner",
               ne."EnteredViaFlowId" AS "EnteredViaFlowId",
               ne."SelectedFlowId" AS "SelectedFlowId",
               ne."ExitedViaFlowId" AS "ExitedViaFlowId",
               mie."WinningFlowId" AS "AggregateFlowId",
               ne."TriggeredBy" AS "StartedBy",
               ne."TriggeredActingFor" AS "StartedActingFor",
               ne."TriggeredDelegationId" AS "StartedDelegationId",
               ne."CompletedBy" AS "CompletedBy",
               ne."CompletedActingFor" AS "CompletedActingFor",
               ne."CompletedDelegationId" AS "CompletedDelegationId",
               ne."CreatedAt" AS "CreatedAt",
               ne."StartedAt" AS "StartedAt",
               ne."UpdatedAt" AS "UpdatedAt",
               ne."CompletedAt" AS "CompletedAt",
               CASE
                   WHEN ne."StartedAt" IS NULL THEN NULL
                   ELSE (EXTRACT(EPOCH FROM (
                       COALESCE(ne."CompletedAt", @asOf) - ne."StartedAt")) * 1000)::bigint
               END AS "DurationMilliseconds",
               ne."IsCutoverSeeded" AS "IsCutoverSeeded",
               ne."NodeRolesJson"::text AS "NodeRolesJson",
               ne."TriggeredByRolesJson"::text AS "StartedByRolesJson",
               ne."CompletedByRolesJson"::text AS "CompletedByRolesJson",
               ne."ErrorCode" AS "ErrorCode",
               ne."ErrorDescription" AS "ErrorDescription",
               ut."RequiresClaim" AS "RequiresClaim",
               ut."RequiresAssignment" AS "RequiresAssignment",
               ut."Assignee" AS "AssignedTo",
               ut."ClaimedBy" AS "ClaimedBy",
               ut."ItemValueJson"::text AS "ItemValueJson",
               ut."ResultJson"::text AS "SubmittedResultJson",
               mie."Mode" AS "MiMode",
               mie."Source" AS "MiSource",
               mie."OnePerActor" AS "MiOnePerActor",
               mie."ResultVariable" AS "MiResultVariable",
               mie."Status" AS "MiStatus",
               mie."TotalCount" AS "MiTotalCount",
               mie."CompletedCount" AS "MiCompletedCount",
               mie."CancelledCount" AS "MiCancelledCount",
               mie."CompletionReason" AS "MiCompletionReason",
               mie."CreatedAt" AS "MiCreatedAt",
               mie."UpdatedAt" AS "MiUpdatedAt",
               mie."CompletedAt" AS "MiCompletedAt"
        """;

    // EF1002: every interpolated fragment below is selected from static
    // allowlists. All caller-controlled values are Npgsql parameters.
#pragma warning disable EF1002
    public async Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(
        NodeExecutionQuery query,
        NodeExecutionAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var asOf = DateTimeOffset.UtcNow;
        var (where, arguments) = BuildWhere(query, authorization, asOf);
        var totalCount = await dbContext.Database
            .SqlQueryRaw<long>(
                $"SELECT COUNT(*) AS \"Value\" {FromSql} {where}",
                BuildParameters(arguments))
            .SingleAsync(cancellationToken);

        if (totalCount == 0)
        {
            return new PagedResult<NodeExecutionSummaryDto>(
                [],
                query.Page,
                query.PageSize,
                totalCount);
        }

        var pageArguments = new List<(string Name, object Value)>(arguments)
        {
            ("take", query.PageSize),
            ("skip", (long)(query.Page - 1) * query.PageSize)
        };
        var rows = await dbContext.Database
            .SqlQueryRaw<NodeExecutionPageRow>(
                $"""
                {ProjectionSql}
                {FromSql}
                {where}
                ORDER BY {BuildOrderBy(query.Sort)}
                LIMIT @take OFFSET @skip
                """,
                BuildParameters(pageArguments))
            .ToListAsync(cancellationToken);

        return new PagedResult<NodeExecutionSummaryDto>(
            rows.Select(ToSummary).ToList(),
            query.Page,
            query.PageSize,
            totalCount);
    }

    public async Task<NodeExecutionDetailDto?> GetAsync(
        long id,
        NodeExecutionAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var query = new NodeExecutionQuery
        {
            ExecutionId = id,
            NodeTypes = [],
            Statuses = [],
            InstanceStatuses = [],
            CompletionReasons = [],
            VariableFilter = null,
            Sort =
            [
                new NodeExecutionSortCriterion(
                    NodeExecutionSortField.Id,
                    SortDirection.Ascending)
            ],
            Page = 1,
            PageSize = 1
        };
        var asOf = DateTimeOffset.UtcNow;
        var (where, arguments) = BuildWhere(query, authorization, asOf);
        var row = await dbContext.Database
            .SqlQueryRaw<NodeExecutionPageRow>(
                $"{ProjectionSql} {FromSql} {where} LIMIT 1",
                BuildParameters(arguments))
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            // Deliberately indistinguishable from a missing id.
            return null;
        }

        var variableRows = await dbContext.Database
            .SqlQueryRaw<NodeExecutionVariableRow>(
                """
                SELECT v."Id" AS "Id",
                       v."VariableName" AS "VariableName",
                       v."SourceActionId" AS "SourceActionId",
                       v."SetBy" AS "SetBy",
                       v."ActingFor" AS "ActingFor",
                       v."DelegationId" AS "DelegationId",
                       v."ValueJson"::text AS "ValueJson",
                       v."SetAt" AS "SetAt"
                FROM flowbit.instance_variables v
                WHERE v."NodeExecutionId" = @nodeExecutionId
                  AND v."InstanceId" = @instanceId
                ORDER BY v."Id"
                """,
                new NpgsqlParameter("nodeExecutionId", id),
                new NpgsqlParameter("instanceId", row.InstanceId))
            .ToListAsync(cancellationToken);

        return ToDetail(
            row,
            variableRows.Select(ToVariableChange).ToList());
    }
#pragma warning restore EF1002

    private static (StringBuilder Where, List<(string Name, object Value)> Arguments)
        BuildWhere(
            NodeExecutionQuery query,
            NodeExecutionAuthorization authorization,
            DateTimeOffset asOf)
    {
        var where = new StringBuilder("""
            WHERE (
                    @isGlobalReader
                 OR EXISTS (
                        SELECT 1
                        FROM jsonb_array_elements_text(
                            CASE
                                WHEN jsonb_typeof(d."Definition" -> 'taskAssignmentRoles') = 'array'
                                THEN d."Definition" -> 'taskAssignmentRoles'
                                ELSE '[]'::jsonb
                            END) AS workflow_reader_role
                        WHERE lower(workflow_reader_role) = ANY(@lowerCallerRoles)
                    )
                  )
            """);
        var arguments = new List<(string Name, object Value)>
        {
            ("isGlobalReader", authorization.IsGlobalReader),
            ("lowerCallerRoles", authorization.LowerCallerRoles.ToArray()),
            ("asOf", asOf)
        };

        AppendEqual(where, arguments, "ne.\"Id\"", "executionId", query.ExecutionId);
        AppendEqual(where, arguments, "ne.\"InstanceId\"", "instanceId", query.InstanceId);
        AppendEqual(where, arguments, "d.\"Id\"", "workflowId", query.WorkflowId);
        AppendEqual(where, arguments, "d.\"Version\"", "workflowVersion", query.WorkflowVersion);
        AppendEqual(where, arguments, "ne.\"ExecutionTokenId\"", "tokenId", query.TokenId);
        AppendEqual(where, arguments, "ne.\"UserTaskId\"", "userTaskId", query.UserTaskId);
        AppendEqual(
            where,
            arguments,
            "ne.\"MultiInstanceExecutionId\"",
            "multiInstanceExecutionId",
            query.MultiInstanceExecutionId);
        AppendEqual(where, arguments, "ne.\"ItemIndex\"", "itemIndex", query.ItemIndex);
        AppendEqual(where, arguments, "ne.\"NodeId\"", "nodeId", query.NodeId);
        AppendEqual(
            where,
            arguments,
            "ne.\"EnteredViaFlowId\"",
            "enteredViaFlowId",
            query.EnteredViaFlowId);
        AppendEqual(
            where,
            arguments,
            "ne.\"SelectedFlowId\"",
            "selectedFlowId",
            query.SelectedFlowId);
        AppendEqual(
            where,
            arguments,
            "ne.\"ExitedViaFlowId\"",
            "exitedViaFlowId",
            query.ExitedViaFlowId);
        AppendEqual(
            where,
            arguments,
            "mie.\"WinningFlowId\"",
            "aggregateFlowId",
            query.AggregateFlowId);
        AppendExact(where, arguments, "d.\"WorkflowKey\"", "workflowKey", query.WorkflowKey);
        AppendExact(where, arguments, "w.\"BusinessKey\"", "businessKey", query.BusinessKey);
        AppendExact(
            where,
            arguments,
            "ne.\"ExecutionKind\"",
            "executionKind",
            query.ExecutionKind);
        AppendCaseInsensitive(
            where,
            arguments,
            "ne.\"NodeName\"",
            "nodeName",
            query.NodeName);
        AppendCaseInsensitive(
            where,
            arguments,
            "ne.\"NodeExternalId\"",
            "nodeExternalId",
            query.NodeExternalId);
        AppendCaseInsensitive(
            where,
            arguments,
            "COALESCE(ut.\"Assignee\", ut.\"ClaimedBy\")",
            "owner",
            query.Owner);
        AppendCaseInsensitive(
            where,
            arguments,
            "ne.\"TriggeredBy\"",
            "startedBy",
            query.StartedBy);
        AppendCaseInsensitive(
            where,
            arguments,
            "ne.\"CompletedBy\"",
            "completedBy",
            query.CompletedBy);

        if (query.GatewayBranchId is long gatewayBranchId)
        {
            arguments.Add(("gatewayBranchId", gatewayBranchId));
            where.Append(
                " AND (ne.\"EntryGatewayBranchId\" = @gatewayBranchId" +
                " OR ne.\"ExitGatewayBranchId\" = @gatewayBranchId)");
        }

        AppendArrayFilter(
            where,
            arguments,
            "ne.\"NodeType\"",
            "nodeTypes",
            query.NodeTypes);
        AppendArrayFilter(
            where,
            arguments,
            "ne.\"Status\"",
            "statuses",
            query.Statuses);
        AppendArrayFilter(
            where,
            arguments,
            "w.\"Status\"",
            "instanceStatuses",
            query.InstanceStatuses);
        AppendArrayFilter(
            where,
            arguments,
            "ne.\"CompletionReason\"",
            "completionReasons",
            query.CompletionReasons);

        if (query.IsMultiInstance is bool isMultiInstance)
        {
            where.Append(isMultiInstance
                ? " AND ne.\"MultiInstanceExecutionId\" IS NOT NULL"
                : " AND ne.\"MultiInstanceExecutionId\" IS NULL");
        }
        if (query.IsCutoverSeeded is bool isCutoverSeeded)
        {
            arguments.Add(("isCutoverSeeded", isCutoverSeeded));
            where.Append(" AND ne.\"IsCutoverSeeded\" = @isCutoverSeeded");
        }

        AppendTimestampRange(
            where,
            arguments,
            "ne.\"CreatedAt\"",
            "created",
            query.CreatedFrom,
            query.CreatedTo);
        AppendTimestampRange(
            where,
            arguments,
            "ne.\"StartedAt\"",
            "started",
            query.StartedFrom,
            query.StartedTo);
        AppendTimestampRange(
            where,
            arguments,
            "ne.\"UpdatedAt\"",
            "updated",
            query.UpdatedFrom,
            query.UpdatedTo);
        AppendTimestampRange(
            where,
            arguments,
            "ne.\"CompletedAt\"",
            "completed",
            query.CompletedFrom,
            query.CompletedTo);

        if (query.MinDurationMilliseconds is long minimumDuration)
        {
            arguments.Add(("minimumDuration", minimumDuration));
            where.Append($" AND ne.\"StartedAt\" IS NOT NULL AND {DurationSql} >= @minimumDuration");
        }
        if (query.MaxDurationMilliseconds is long maximumDuration)
        {
            arguments.Add(("maximumDuration", maximumDuration));
            where.Append($" AND ne.\"StartedAt\" IS NOT NULL AND {DurationSql} <= @maximumDuration");
        }

        VariableFilterSqlCompiler.Append(where, arguments, query.VariableFilter, "w.\"Id\"");
        return (where, arguments);
    }

    private static void AppendEqual<T>(
        StringBuilder where,
        List<(string Name, object Value)> arguments,
        string column,
        string name,
        T? value)
        where T : struct
    {
        if (value is null)
        {
            return;
        }
        arguments.Add((name, value.Value));
        where.Append($" AND {column} = @{name}");
    }

    private static void AppendExact(
        StringBuilder where,
        List<(string Name, object Value)> arguments,
        string column,
        string name,
        string? value)
    {
        if (value is null)
        {
            return;
        }
        arguments.Add((name, value));
        where.Append($" AND {column} = @{name}");
    }

    private static void AppendCaseInsensitive(
        StringBuilder where,
        List<(string Name, object Value)> arguments,
        string column,
        string name,
        string? value)
    {
        if (value is null)
        {
            return;
        }
        arguments.Add((name, value));
        where.Append($" AND lower({column}) = lower(@{name})");
    }

    private static void AppendArrayFilter(
        StringBuilder where,
        List<(string Name, object Value)> arguments,
        string column,
        string name,
        IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }
        arguments.Add((name, values.ToArray()));
        where.Append($" AND {column} = ANY(@{name})");
    }

    private static void AppendTimestampRange(
        StringBuilder where,
        List<(string Name, object Value)> arguments,
        string column,
        string name,
        DateTimeOffset? from,
        DateTimeOffset? to)
    {
        if (from is DateTimeOffset lower)
        {
            arguments.Add(($"{name}From", lower));
            where.Append($" AND {column} >= @{name}From");
        }
        if (to is DateTimeOffset upper)
        {
            arguments.Add(($"{name}To", upper));
            where.Append($" AND {column} < @{name}To");
        }
    }

    private static string BuildOrderBy(IReadOnlyList<NodeExecutionSortCriterion> sort)
    {
        var criteria = sort.Count == 0
            ?
            [
                new NodeExecutionSortCriterion(
                    NodeExecutionSortField.UpdatedAt,
                    SortDirection.Descending),
                new NodeExecutionSortCriterion(
                    NodeExecutionSortField.Id,
                    SortDirection.Descending)
            ]
            : sort;

        var parts = criteria.Select(criterion =>
        {
            var column = criterion.Field switch
            {
                NodeExecutionSortField.Id => "ne.\"Id\"",
                NodeExecutionSortField.InstanceId => "ne.\"InstanceId\"",
                NodeExecutionSortField.WorkflowId => "ne.\"WorkflowDefinitionId\"",
                NodeExecutionSortField.NodeId => "ne.\"NodeId\"",
                NodeExecutionSortField.CreatedAt => "ne.\"CreatedAt\"",
                NodeExecutionSortField.StartedAt => "ne.\"StartedAt\"",
                NodeExecutionSortField.UpdatedAt => "ne.\"UpdatedAt\"",
                NodeExecutionSortField.CompletedAt => "ne.\"CompletedAt\"",
                NodeExecutionSortField.Duration => DurationSql,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(sort),
                    criterion.Field,
                    "Unsupported node-execution sort field.")
            };
            var direction = criterion.Direction switch
            {
                SortDirection.Ascending => "ASC",
                SortDirection.Descending => "DESC",
                _ => throw new ArgumentOutOfRangeException(
                    nameof(sort),
                    criterion.Direction,
                    "Unsupported sort direction.")
            };
            return $"{column} {direction} NULLS LAST";
        });
        return string.Join(", ", parts);
    }

    private static NpgsqlParameter[] BuildParameters(
        IEnumerable<(string Name, object Value)> arguments) =>
        arguments
            .Select(static argument => new NpgsqlParameter(argument.Name, argument.Value))
            .ToArray();

    private static NodeExecutionSummaryDto ToSummary(NodeExecutionPageRow row) =>
        new()
        {
            Id = row.Id,
            InstanceId = row.InstanceId,
            WorkflowId = row.WorkflowId,
            WorkflowKey = row.WorkflowKey,
            WorkflowName = row.WorkflowName,
            WorkflowVersion = row.WorkflowVersion,
            BusinessKey = row.BusinessKey,
            TokenId = row.TokenId,
            UserTaskId = row.UserTaskId,
            MultiInstanceExecutionId = row.MultiInstanceExecutionId,
            ItemIndex = row.ItemIndex,
            EntryGatewayBranchId = row.EntryGatewayBranchId,
            ExitGatewayBranchId = row.ExitGatewayBranchId,
            ExecutionKind = row.ExecutionKind,
            NodeId = row.NodeId,
            NodeName = row.NodeName,
            NodeExternalId = row.NodeExternalId,
            NodeType = row.NodeType,
            Status = row.Status,
            InstanceStatus = row.InstanceStatus,
            CompletionReason = row.CompletionReason,
            IsMultiInstance = row.IsMultiInstance,
            Owner = row.Owner,
            EnteredViaFlowId = row.EnteredViaFlowId,
            SelectedFlowId = row.SelectedFlowId,
            ExitedViaFlowId = row.ExitedViaFlowId,
            AggregateFlowId = row.AggregateFlowId,
            StartedBy = row.StartedBy,
            StartedDelegatedAccess = ToDelegatedAccess(
                row.StartedDelegationId, row.StartedActingFor),
            CompletedBy = row.CompletedBy,
            CompletedDelegatedAccess = ToDelegatedAccess(
                row.CompletedDelegationId, row.CompletedActingFor),
            CreatedAt = row.CreatedAt,
            StartedAt = row.StartedAt,
            UpdatedAt = row.UpdatedAt,
            CompletedAt = row.CompletedAt,
            DurationMilliseconds = row.DurationMilliseconds,
            IsCutoverSeeded = row.IsCutoverSeeded
        };

    private static NodeExecutionDetailDto ToDetail(
        NodeExecutionPageRow row,
        IReadOnlyList<NodeExecutionVariableChangeDto> variableChanges) =>
        new()
        {
            Id = row.Id,
            InstanceId = row.InstanceId,
            WorkflowId = row.WorkflowId,
            WorkflowKey = row.WorkflowKey,
            WorkflowName = row.WorkflowName,
            WorkflowVersion = row.WorkflowVersion,
            BusinessKey = row.BusinessKey,
            TokenId = row.TokenId,
            UserTaskId = row.UserTaskId,
            MultiInstanceExecutionId = row.MultiInstanceExecutionId,
            ItemIndex = row.ItemIndex,
            EntryGatewayBranchId = row.EntryGatewayBranchId,
            ExitGatewayBranchId = row.ExitGatewayBranchId,
            ExecutionKind = row.ExecutionKind,
            NodeId = row.NodeId,
            NodeName = row.NodeName,
            NodeExternalId = row.NodeExternalId,
            NodeType = row.NodeType,
            Status = row.Status,
            InstanceStatus = row.InstanceStatus,
            CompletionReason = row.CompletionReason,
            IsMultiInstance = row.IsMultiInstance,
            Owner = row.Owner,
            EnteredViaFlowId = row.EnteredViaFlowId,
            SelectedFlowId = row.SelectedFlowId,
            ExitedViaFlowId = row.ExitedViaFlowId,
            AggregateFlowId = row.AggregateFlowId,
            StartedBy = row.StartedBy,
            StartedDelegatedAccess = ToDelegatedAccess(
                row.StartedDelegationId, row.StartedActingFor),
            CompletedBy = row.CompletedBy,
            CompletedDelegatedAccess = ToDelegatedAccess(
                row.CompletedDelegationId, row.CompletedActingFor),
            CreatedAt = row.CreatedAt,
            StartedAt = row.StartedAt,
            UpdatedAt = row.UpdatedAt,
            CompletedAt = row.CompletedAt,
            DurationMilliseconds = row.DurationMilliseconds,
            IsCutoverSeeded = row.IsCutoverSeeded,
            NodeRoles = ParseStringArray(row.NodeRolesJson),
            StartedByRoles = ParseStringArray(row.StartedByRolesJson),
            CompletedByRoles = ParseStringArray(row.CompletedByRolesJson),
            RequiresClaim = row.RequiresClaim,
            RequiresAssignment = row.RequiresAssignment,
            AssignedTo = row.AssignedTo,
            ClaimedBy = row.ClaimedBy,
            ItemValue = ParseElement(row.ItemValueJson),
            SubmittedResult = ParseDictionary(row.SubmittedResultJson),
            MultiInstance = ToMultiInstance(row),
            Error = row.ErrorCode is null && row.ErrorDescription is null
                ? null
                : new NodeExecutionErrorDto(row.ErrorCode, row.ErrorDescription),
            VariableChanges = variableChanges
        };

    private static NodeExecutionMultiInstanceDto? ToMultiInstance(
        NodeExecutionPageRow row)
    {
        if (row.MultiInstanceExecutionId is not long executionId)
        {
            return null;
        }
        return new NodeExecutionMultiInstanceDto(
            executionId,
            row.MiMode!,
            row.MiSource!,
            row.MiOnePerActor ?? false,
            row.MiResultVariable!,
            row.MiStatus!,
            row.MiTotalCount ?? 0,
            row.MiCompletedCount ?? 0,
            row.MiCancelledCount ?? 0,
            row.AggregateFlowId,
            row.MiCompletionReason,
            row.MiCreatedAt ?? row.CreatedAt,
            row.MiUpdatedAt ?? row.UpdatedAt,
            row.MiCompletedAt);
    }

    private static NodeExecutionVariableChangeDto ToVariableChange(NodeExecutionVariableRow row) =>
        new NodeExecutionVariableChangeDto(
            row.Id,
            row.VariableName,
            row.SourceActionId,
            row.SetBy,
            ParseRequiredElement(row.ValueJson),
            row.SetAt)
        {
            DelegatedAccess = ToDelegatedAccess(row.DelegationId, row.ActingFor)
        };

    private static DelegatedTaskAccessDto? ToDelegatedAccess(
        long? delegationId,
        string? actingFor) =>
        delegationId is long id && !string.IsNullOrWhiteSpace(actingFor)
            ? new DelegatedTaskAccessDto(id, actingFor)
            : null;

    private static IReadOnlyList<string>? ParseStringArray(string? json)
    {
        if (json is null)
        {
            return null;
        }
        return JsonSerializer.Deserialize<string[]>(json) ?? [];
    }

    private static JsonElement? ParseElement(string? json)
    {
        if (json is null)
        {
            return null;
        }
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement ParseRequiredElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static IReadOnlyDictionary<string, JsonElement>? ParseDictionary(
        string? json)
    {
        if (json is null)
        {
            return null;
        }
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
    }

    private sealed class NodeExecutionPageRow
    {
        public long Id { get; set; }
        public long InstanceId { get; set; }
        public long WorkflowId { get; set; }
        public string WorkflowKey { get; set; } = string.Empty;
        public string WorkflowName { get; set; } = string.Empty;
        public int WorkflowVersion { get; set; }
        public string? BusinessKey { get; set; }
        public long TokenId { get; set; }
        public long? UserTaskId { get; set; }
        public long? MultiInstanceExecutionId { get; set; }
        public int? ItemIndex { get; set; }
        public long? EntryGatewayBranchId { get; set; }
        public long? ExitGatewayBranchId { get; set; }
        public string ExecutionKind { get; set; } = string.Empty;
        public int NodeId { get; set; }
        public string NodeName { get; set; } = string.Empty;
        public string? NodeExternalId { get; set; }
        public string NodeType { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string InstanceStatus { get; set; } = string.Empty;
        public string? CompletionReason { get; set; }
        public bool IsMultiInstance { get; set; }
        public string? Owner { get; set; }
        public int? EnteredViaFlowId { get; set; }
        public int? SelectedFlowId { get; set; }
        public int? ExitedViaFlowId { get; set; }
        public int? AggregateFlowId { get; set; }
        public string? StartedBy { get; set; }
        public string? StartedActingFor { get; set; }
        public long? StartedDelegationId { get; set; }
        public string? CompletedBy { get; set; }
        public string? CompletedActingFor { get; set; }
        public long? CompletedDelegationId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public long? DurationMilliseconds { get; set; }
        public bool IsCutoverSeeded { get; set; }
        public string? NodeRolesJson { get; set; }
        public string? StartedByRolesJson { get; set; }
        public string? CompletedByRolesJson { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorDescription { get; set; }
        public bool? RequiresClaim { get; set; }
        public bool? RequiresAssignment { get; set; }
        public string? AssignedTo { get; set; }
        public string? ClaimedBy { get; set; }
        public string? ItemValueJson { get; set; }
        public string? SubmittedResultJson { get; set; }
        public string? MiMode { get; set; }
        public string? MiSource { get; set; }
        public bool? MiOnePerActor { get; set; }
        public string? MiResultVariable { get; set; }
        public string? MiStatus { get; set; }
        public int? MiTotalCount { get; set; }
        public int? MiCompletedCount { get; set; }
        public int? MiCancelledCount { get; set; }
        public string? MiCompletionReason { get; set; }
        public DateTimeOffset? MiCreatedAt { get; set; }
        public DateTimeOffset? MiUpdatedAt { get; set; }
        public DateTimeOffset? MiCompletedAt { get; set; }
    }

    private sealed class NodeExecutionVariableRow
    {
        public long Id { get; set; }
        public string VariableName { get; set; } = string.Empty;
        public int? SourceActionId { get; set; }
        public string? SetBy { get; set; }
        public string? ActingFor { get; set; }
        public long? DelegationId { get; set; }
        public string ValueJson { get; set; } = "null";
        public DateTimeOffset SetAt { get; set; }
    }
}
