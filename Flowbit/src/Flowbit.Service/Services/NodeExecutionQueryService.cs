using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Validates node-execution search input and resolves the caller's dynamic,
/// read-only visibility scope. All row authorization, counting, ordering, and
/// paging are delegated to one SQL-backed repository query.
/// </summary>
public sealed class NodeExecutionQueryService(
    INodeExecutionQueryRepository repository,
    IEngineSettingsRepository engineSettings) : INodeExecutionQueryService
{
    public const string RequiredRoleSettingKey = "NodeExecution.RequiredRole";
    public const string DefaultRequiredRole = "admin";

    private const int MaxVariableFilters = 10;
    private const int MaxSortCriteria = 3;

    private static readonly IReadOnlyDictionary<string, string> ExecutionKinds =
        CanonicalMap(NodeExecutionRecordKinds.Node, NodeExecutionRecordKinds.UserTaskItem);

    private static readonly IReadOnlyDictionary<string, string> ExecutionStatuses =
        CanonicalMap(
            NodeExecutionRecordStatuses.Pending,
            NodeExecutionRecordStatuses.Active,
            NodeExecutionRecordStatuses.Completed,
            NodeExecutionRecordStatuses.Cancelled,
            NodeExecutionRecordStatuses.Faulted,
            NodeExecutionRecordStatuses.Merged);

    private static readonly IReadOnlyDictionary<string, string> InstanceStatuses =
        CanonicalMap(
            WorkflowInstanceStatuses.Running,
            WorkflowInstanceStatuses.Completed,
            WorkflowInstanceStatuses.Cancelled,
            WorkflowInstanceStatuses.Faulted);

    private static readonly IReadOnlyDictionary<string, string> CompletionReasons =
        CanonicalMap(
            NodeExecutionCompletionReasons.Normal,
            NodeExecutionCompletionReasons.UserAction,
            NodeExecutionCompletionReasons.MessageDelivery,
            NodeExecutionCompletionReasons.MultiInstanceItem,
            NodeExecutionCompletionReasons.MultiInstanceCompleted,
            NodeExecutionCompletionReasons.MultiInstanceInterrupt,
            NodeExecutionCompletionReasons.BoundaryCaught,
            NodeExecutionCompletionReasons.NormalEnd,
            NodeExecutionCompletionReasons.TerminateEnd,
            NodeExecutionCompletionReasons.ErrorEnd,
            NodeExecutionCompletionReasons.InstanceCancelled,
            NodeExecutionCompletionReasons.GatewayScopeCancelled,
            NodeExecutionCompletionReasons.GatewayJoinMerged,
            NodeExecutionCompletionReasons.ParallelFork,
            NodeExecutionCompletionReasons.ParallelJoin,
            NodeExecutionCompletionReasons.InclusiveSplit,
            NodeExecutionCompletionReasons.InclusiveMerge,
            NodeExecutionCompletionReasons.ComplexActivation,
            NodeExecutionCompletionReasons.ComplexReset,
            NodeExecutionCompletionReasons.ScopedInterrupt,
            NodeExecutionCompletionReasons.ScopedInterruptSkipped);

    private static readonly IReadOnlyDictionary<string, string> NodeTypes =
        CanonicalMap(
            BpmnFlowNodeTypes.StartEvent,
            BpmnFlowNodeTypes.MessageStartEvent,
            BpmnFlowNodeTypes.UserTask,
            BpmnFlowNodeTypes.Task,
            BpmnFlowNodeTypes.ServiceTask,
            BpmnFlowNodeTypes.ScriptTask,
            BpmnFlowNodeTypes.ExclusiveGateway,
            BpmnFlowNodeTypes.ParallelGateway,
            BpmnFlowNodeTypes.InclusiveGateway,
            BpmnFlowNodeTypes.ComplexGateway,
            BpmnFlowNodeTypes.ScopedInterruptEvent,
            BpmnFlowNodeTypes.ErrorBoundaryEvent,
            BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
            BpmnFlowNodeTypes.EndEvent,
            BpmnFlowNodeTypes.TerminateEndEvent,
            BpmnFlowNodeTypes.ErrorEndEvent);

    public async Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(
        NodeExecutionSearchRequest request,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);

        var query = Normalize(request);
        var authorization = await ResolveAuthorizationAsync(actor, cancellationToken);
        return await repository.SearchAsync(query, authorization, cancellationToken);
    }

    public async Task<NodeExecutionDetailDto?> GetAsync(
        long id,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ValidatePositive(id, "node execution id");
        ArgumentNullException.ThrowIfNull(actor);

        var authorization = await ResolveAuthorizationAsync(actor, cancellationToken);
        return await repository.GetAsync(id, authorization, cancellationToken);
    }

    private async Task<NodeExecutionAuthorization> ResolveAuthorizationAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        var setting = await engineSettings.GetByKeyAsync(RequiredRoleSettingKey, cancellationToken);
        var configuredRoles = ParseConfiguredGlobalRoles(setting?.Value);
        var lowerCallerRoles = actor.Roles
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Select(static role => role.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var isGlobalReader = configuredRoles
            .Select(static role => role.ToLowerInvariant())
            .Intersect(lowerCallerRoles, StringComparer.Ordinal)
            .Any();

        return new NodeExecutionAuthorization(isGlobalReader, lowerCallerRoles);
    }

    internal static IReadOnlyList<string> ParseConfiguredGlobalRoles(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [DefaultRequiredRole];
        }

        var roles = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static role => role.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return roles.Length == 0 ? [DefaultRequiredRole] : roles;
    }

    internal static NodeExecutionQuery Normalize(NodeExecutionSearchRequest request)
    {
        ValidateNullablePositive(request.ExecutionId, "node execution id");
        ValidateNullablePositive(request.InstanceId, "instance id");
        ValidateNullablePositive(request.WorkflowId, "workflow id");
        ValidateNullablePositive(request.WorkflowVersion, "workflow version");
        ValidateNullablePositive(request.TokenId, "token id");
        ValidateNullablePositive(request.UserTaskId, "user task id");
        ValidateNullablePositive(request.MultiInstanceExecutionId, "multi-instance execution id");
        ValidateNullablePositive(request.GatewayBranchId, "gateway branch id");
        ValidateNullableNonNegative(request.ItemIndex, "item index");
        ValidateNullablePositive(request.NodeId, "node id");
        ValidateNullablePositive(request.EnteredViaFlowId, "entered-via flow id");
        ValidateNullablePositive(request.SelectedFlowId, "selected flow id");
        ValidateNullablePositive(request.ExitedViaFlowId, "exited-via flow id");
        ValidateNullablePositive(request.AggregateFlowId, "aggregate flow id");

        ValidateRange(request.CreatedFrom, request.CreatedTo, "created");
        ValidateRange(request.StartedFrom, request.StartedTo, "started");
        ValidateRange(request.UpdatedFrom, request.UpdatedTo, "updated");
        ValidateRange(request.CompletedFrom, request.CompletedTo, "completed");

        if (request.MinDurationMilliseconds is < 0)
        {
            throw new WorkflowDomainException("Minimum duration must not be negative.");
        }
        if (request.MaxDurationMilliseconds is < 0)
        {
            throw new WorkflowDomainException("Maximum duration must not be negative.");
        }
        if (request.MinDurationMilliseconds is long minimum
            && request.MaxDurationMilliseconds is long maximum
            && minimum > maximum)
        {
            throw new WorkflowDomainException(
                "Minimum duration must be less than or equal to maximum duration.");
        }

        return new NodeExecutionQuery
        {
            ExecutionId = request.ExecutionId,
            InstanceId = request.InstanceId,
            WorkflowId = request.WorkflowId,
            WorkflowKey = TrimToNull(request.WorkflowKey),
            WorkflowVersion = request.WorkflowVersion,
            BusinessKey = TrimToNull(request.BusinessKey),
            TokenId = request.TokenId,
            UserTaskId = request.UserTaskId,
            MultiInstanceExecutionId = request.MultiInstanceExecutionId,
            GatewayBranchId = request.GatewayBranchId,
            ItemIndex = request.ItemIndex,
            ExecutionKind = ParseOptionalEnum(request.ExecutionKind, ExecutionKinds, "execution kind"),
            NodeId = request.NodeId,
            NodeName = TrimToNull(request.NodeName),
            NodeExternalId = TrimToNull(request.NodeExternalId),
            NodeTypes = ParseEnumGroup(request.NodeTypes, NodeTypes, "node type"),
            Statuses = ParseEnumGroup(request.Statuses, ExecutionStatuses, "execution status"),
            InstanceStatuses = ParseEnumGroup(
                request.InstanceStatuses,
                InstanceStatuses,
                "instance status"),
            CompletionReasons = ParseEnumGroup(
                request.CompletionReasons,
                CompletionReasons,
                "completion reason"),
            IsMultiInstance = request.IsMultiInstance,
            IsCutoverSeeded = request.IsCutoverSeeded,
            Owner = TrimToNull(request.Owner),
            StartedBy = TrimToNull(request.StartedBy),
            CompletedBy = TrimToNull(request.CompletedBy),
            EnteredViaFlowId = request.EnteredViaFlowId,
            SelectedFlowId = request.SelectedFlowId,
            ExitedViaFlowId = request.ExitedViaFlowId,
            AggregateFlowId = request.AggregateFlowId,
            CreatedFrom = request.CreatedFrom?.ToUniversalTime(),
            CreatedTo = request.CreatedTo?.ToUniversalTime(),
            StartedFrom = request.StartedFrom?.ToUniversalTime(),
            StartedTo = request.StartedTo?.ToUniversalTime(),
            UpdatedFrom = request.UpdatedFrom?.ToUniversalTime(),
            UpdatedTo = request.UpdatedTo?.ToUniversalTime(),
            CompletedFrom = request.CompletedFrom?.ToUniversalTime(),
            CompletedTo = request.CompletedTo?.ToUniversalTime(),
            MinDurationMilliseconds = request.MinDurationMilliseconds,
            MaxDurationMilliseconds = request.MaxDurationMilliseconds,
            VariableFilters = ParseVariableFilters(request.Variables),
            Sort = ParseSort(request.Sort),
            Page = Math.Max(1, request.Page),
            PageSize = Math.Clamp(request.PageSize, 1, 200)
        };
    }

    private static IReadOnlyList<VariableFilter> ParseVariableFilters(
        IReadOnlyList<string>? variables)
    {
        if (variables is null || variables.Count == 0)
        {
            return [];
        }
        if (variables.Count > MaxVariableFilters)
        {
            throw new WorkflowDomainException(
                $"At most {MaxVariableFilters} variable filters are allowed.");
        }

        var result = new List<VariableFilter>(variables.Count);
        foreach (var raw in variables)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw new WorkflowDomainException(
                    "Variable filters must not be blank. Expected format 'name:value'.");
            }

            var separator = raw.IndexOf(':');
            if (separator <= 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid variable filter '{raw}'. Expected format 'name:value'.");
            }

            var name = raw[..separator].Trim();
            var value = raw[(separator + 1)..].Trim();
            if (name.Length == 0)
            {
                throw new WorkflowDomainException(
                    $"Invalid variable filter '{raw}'. Variable name is required.");
            }
            if (name.Length > 300)
            {
                throw new WorkflowDomainException(
                    $"Invalid variable filter '{raw}'. Variable names cannot exceed 300 characters.");
            }

            result.Add(new VariableFilter(name, value));
        }
        return result;
    }

    private static IReadOnlyList<NodeExecutionSortCriterion> ParseSort(
        IReadOnlyList<string>? rawSort)
    {
        if (rawSort is null || rawSort.Count == 0)
        {
            return
            [
                new NodeExecutionSortCriterion(
                    NodeExecutionSortField.UpdatedAt,
                    SortDirection.Descending),
                new NodeExecutionSortCriterion(
                    NodeExecutionSortField.Id,
                    SortDirection.Descending)
            ];
        }
        if (rawSort.Count > MaxSortCriteria)
        {
            throw new WorkflowDomainException(
                $"At most {MaxSortCriteria} sort clauses are allowed.");
        }

        var result = new List<NodeExecutionSortCriterion>(rawSort.Count);
        var seen = new HashSet<NodeExecutionSortField>();
        foreach (var raw in rawSort)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                throw InvalidSort(raw ?? string.Empty);
            }

            var separator = raw.IndexOf(':');
            if (separator <= 0
                || separator == raw.Length - 1
                || raw.IndexOf(':', separator + 1) >= 0)
            {
                throw InvalidSort(raw);
            }

            var fieldText = raw[..separator].Trim();
            var directionText = raw[(separator + 1)..].Trim();
            var field = fieldText.ToLowerInvariant() switch
            {
                "id" => NodeExecutionSortField.Id,
                "instanceid" => NodeExecutionSortField.InstanceId,
                "workflowid" => NodeExecutionSortField.WorkflowId,
                "nodeid" => NodeExecutionSortField.NodeId,
                "createdat" => NodeExecutionSortField.CreatedAt,
                "startedat" => NodeExecutionSortField.StartedAt,
                "updatedat" => NodeExecutionSortField.UpdatedAt,
                "completedat" => NodeExecutionSortField.CompletedAt,
                "duration" or "durationmilliseconds" => NodeExecutionSortField.Duration,
                _ => throw new WorkflowDomainException(
                    $"Unknown node execution sort field '{fieldText}'. Allowed fields: " +
                    "id, instanceId, workflowId, nodeId, createdAt, startedAt, " +
                    "updatedAt, completedAt, duration.")
            };
            if (!seen.Add(field))
            {
                throw new WorkflowDomainException(
                    $"Sort field '{fieldText}' was specified more than once.");
            }

            var direction = directionText.ToLowerInvariant() switch
            {
                "asc" => SortDirection.Ascending,
                "desc" => SortDirection.Descending,
                _ => throw new WorkflowDomainException(
                    $"Unknown sort direction '{directionText}'. Allowed directions: asc, desc.")
            };
            result.Add(new NodeExecutionSortCriterion(field, direction));
        }

        if (!seen.Contains(NodeExecutionSortField.Id))
        {
            result.Add(new NodeExecutionSortCriterion(
                NodeExecutionSortField.Id,
                result[^1].Direction));
        }
        return result;
    }

    private static WorkflowDomainException InvalidSort(string raw) =>
        new($"Invalid sort clause '{raw}'. Expected format 'field:asc' or 'field:desc'.");

    private static string? ParseOptionalEnum(
        string? value,
        IReadOnlyDictionary<string, string> allowed,
        string label)
    {
        var normalized = TrimToNull(value);
        if (normalized is null)
        {
            return null;
        }
        if (allowed.TryGetValue(normalized, out var canonical))
        {
            return canonical;
        }
        throw new WorkflowDomainException($"Unknown {label} '{value}'.");
    }

    private static IReadOnlyList<string> ParseEnumGroup(
        IReadOnlyList<string>? values,
        IReadOnlyDictionary<string, string> allowed,
        string label)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var result = new List<string>(values.Count);
        foreach (var value in values)
        {
            var canonical = ParseOptionalEnum(value, allowed, label)
                ?? throw new WorkflowDomainException($"{label} filters must not be blank.");
            if (!result.Contains(canonical, StringComparer.Ordinal))
            {
                result.Add(canonical);
            }
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> CanonicalMap(params string[] values) =>
        values.ToDictionary(static value => value, static value => value, StringComparer.OrdinalIgnoreCase);

    private static string? TrimToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void ValidateRange(
        DateTimeOffset? from,
        DateTimeOffset? to,
        string label)
    {
        if (from is not null && to is not null && from.Value >= to.Value)
        {
            throw new WorkflowDomainException(
                $"{label}From must be earlier than {label}To.");
        }
    }

    private static void ValidateNullablePositive(long? value, string label)
    {
        if (value is not null)
        {
            ValidatePositive(value.Value, label);
        }
    }

    private static void ValidateNullablePositive(int? value, string label)
    {
        if (value is not null)
        {
            ValidatePositive(value.Value, label);
        }
    }

    private static void ValidateNullableNonNegative(int? value, string label)
    {
        if (value is < 0)
        {
            throw new WorkflowDomainException($"{label} must not be negative.");
        }
    }

    private static void ValidatePositive(long value, string label)
    {
        if (value <= 0)
        {
            throw new WorkflowDomainException($"{label} must be greater than zero.");
        }
    }
}
