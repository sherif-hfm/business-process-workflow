using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Endpoints;

/// <summary>
/// Read-only, cross-workflow activity search. These endpoints do not grant any
/// workflow, instance, assignment, claim, or cancellation authority.
/// </summary>
public static class NodeExecutionEndpoints
{
    public static IEndpointRouteBuilder MapNodeExecutionEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/node-executions")
            .WithTags("Node Executions")
            .RequireAuthorization();

        group.MapGet("/", Search)
            .WithSummary("Search node executions across workflow versions and instances")
            .WithDescription(
                """
                Returns one durable row per token visit. A multi-instance userTask
                returns one row per child work item and no duplicate parent row.
                Repeated status, instanceStatus, nodeType, and completionReason
                filters OR-combine within their group; all distinct groups
                AND-combine. From timestamps are inclusive and To timestamps are
                exclusive. Repeated var=name:value filters inspect the latest
                current scalar value on the owning instance (not an execution-time
                snapshot), compare exactly and case-insensitively, and AND-combine.
                owner is also an exact case-insensitive filter over the effective
                user-task owner: explicit assignment first, otherwise claimant.
                Up to ten variable filters and three unique sort fields are
                accepted. Default order is updatedAt:desc,id:desc; nullable values
                sort NULLS LAST and an id tie-breaker is always present.

                Visibility is evaluated in SQL before count, order, and paging. A
                caller can see a definition version when they hold a role from the
                dynamic NodeExecution.RequiredRole setting (blank/missing defaults
                to admin) or that immutable version's taskAssignmentRoles. The
                returned totalCount is exact for the authorized result set.
                """)
            .Produces<PagedResult<NodeExecutionSummaryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/{id:long}", Get)
            .WithSummary("Get one authorized node execution")
            .WithDescription(
                """
                Returns immutable node/actor snapshots, user-task and
                multi-instance context, submitted result and committed failure
                information, plus only the variable writes attributed to this
                execution. It does not return a current or historical instance
                variable snapshot. Missing and out-of-scope ids both return 404.
                """)
            .Produces<NodeExecutionDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> Search(
        [AsParameters] NodeExecutionHttpQuery query,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        INodeExecutionQueryService service,
        CancellationToken cancellationToken)
    {
        var request = new NodeExecutionSearchRequest
        {
            ExecutionId = query.ExecutionId,
            InstanceId = query.InstanceId,
            WorkflowId = query.WorkflowId,
            WorkflowKey = query.WorkflowKey,
            WorkflowVersion = query.WorkflowVersion,
            BusinessKey = query.BusinessKey,
            TokenId = query.TokenId,
            UserTaskId = query.UserTaskId,
            MultiInstanceExecutionId = query.MultiInstanceExecutionId,
            ParallelBranchId = query.ParallelBranchId,
            ItemIndex = query.ItemIndex,
            ExecutionKind = query.ExecutionKind,
            NodeId = query.NodeId,
            NodeName = query.NodeName,
            NodeExternalId = query.NodeExternalId,
            NodeTypes = query.NodeTypes,
            Statuses = query.Statuses,
            InstanceStatuses = query.InstanceStatuses,
            CompletionReasons = query.CompletionReasons,
            IsMultiInstance = query.IsMultiInstance,
            IsCutoverSeeded = query.IsCutoverSeeded,
            Owner = query.Owner,
            StartedBy = query.StartedBy,
            CompletedBy = query.CompletedBy,
            EnteredViaFlowId = query.EnteredViaFlowId,
            SelectedFlowId = query.SelectedFlowId,
            ExitedViaFlowId = query.ExitedViaFlowId,
            AggregateFlowId = query.AggregateFlowId,
            CreatedFrom = query.CreatedFrom,
            CreatedTo = query.CreatedTo,
            StartedFrom = query.StartedFrom,
            StartedTo = query.StartedTo,
            UpdatedFrom = query.UpdatedFrom,
            UpdatedTo = query.UpdatedTo,
            CompletedFrom = query.CompletedFrom,
            CompletedTo = query.CompletedTo,
            MinDurationMilliseconds = query.MinDurationMilliseconds,
            MaxDurationMilliseconds = query.MaxDurationMilliseconds,
            Variables = query.Variables,
            Sort = query.Sort,
            Page = Math.Max(1, query.Page ?? 1),
            PageSize = Math.Clamp(query.PageSize ?? 50, 1, 200)
        };

        return Results.Ok(await service.SearchAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken));
    }

    private static async Task<IResult> Get(
        long id,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        INodeExecutionQueryService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(
            id,
            actorResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    /// <summary>
    /// Query-string contract for node execution search. Identifier values must be
    /// positive (itemIndex may be zero); malformed values are rejected with 400.
    /// </summary>
    public sealed class NodeExecutionHttpQuery
    {
        [FromQuery(Name = "executionId")]
        public long? ExecutionId { get; init; }

        [FromQuery(Name = "instanceId")]
        public long? InstanceId { get; init; }

        [FromQuery(Name = "workflowId")]
        public long? WorkflowId { get; init; }

        [FromQuery(Name = "workflowKey")]
        public string? WorkflowKey { get; init; }

        [FromQuery(Name = "workflowVersion")]
        public int? WorkflowVersion { get; init; }

        [FromQuery(Name = "businessKey")]
        public string? BusinessKey { get; init; }

        [FromQuery(Name = "tokenId")]
        public long? TokenId { get; init; }

        [FromQuery(Name = "userTaskId")]
        public long? UserTaskId { get; init; }

        [FromQuery(Name = "multiInstanceExecutionId")]
        public long? MultiInstanceExecutionId { get; init; }

        [FromQuery(Name = "parallelBranchId")]
        public long? ParallelBranchId { get; init; }

        [FromQuery(Name = "itemIndex")]
        public int? ItemIndex { get; init; }

        [FromQuery(Name = "executionKind")]
        public string? ExecutionKind { get; init; }

        [FromQuery(Name = "nodeId")]
        public int? NodeId { get; init; }

        [FromQuery(Name = "nodeName")]
        public string? NodeName { get; init; }

        [FromQuery(Name = "nodeExternalId")]
        public string? NodeExternalId { get; init; }

        [FromQuery(Name = "nodeType")]
        public string[]? NodeTypes { get; init; }

        [FromQuery(Name = "status")]
        public string[]? Statuses { get; init; }

        [FromQuery(Name = "instanceStatus")]
        public string[]? InstanceStatuses { get; init; }

        [FromQuery(Name = "completionReason")]
        public string[]? CompletionReasons { get; init; }

        [FromQuery(Name = "isMultiInstance")]
        public bool? IsMultiInstance { get; init; }

        [FromQuery(Name = "isCutoverSeeded")]
        public bool? IsCutoverSeeded { get; init; }

        [FromQuery(Name = "owner")]
        public string? Owner { get; init; }

        [FromQuery(Name = "startedBy")]
        public string? StartedBy { get; init; }

        [FromQuery(Name = "completedBy")]
        public string? CompletedBy { get; init; }

        [FromQuery(Name = "enteredViaFlowId")]
        public int? EnteredViaFlowId { get; init; }

        [FromQuery(Name = "selectedFlowId")]
        public int? SelectedFlowId { get; init; }

        [FromQuery(Name = "exitedViaFlowId")]
        public int? ExitedViaFlowId { get; init; }

        [FromQuery(Name = "aggregateFlowId")]
        public int? AggregateFlowId { get; init; }

        [FromQuery(Name = "createdFrom")]
        public DateTimeOffset? CreatedFrom { get; init; }

        [FromQuery(Name = "createdTo")]
        public DateTimeOffset? CreatedTo { get; init; }

        [FromQuery(Name = "startedFrom")]
        public DateTimeOffset? StartedFrom { get; init; }

        [FromQuery(Name = "startedTo")]
        public DateTimeOffset? StartedTo { get; init; }

        [FromQuery(Name = "updatedFrom")]
        public DateTimeOffset? UpdatedFrom { get; init; }

        [FromQuery(Name = "updatedTo")]
        public DateTimeOffset? UpdatedTo { get; init; }

        [FromQuery(Name = "completedFrom")]
        public DateTimeOffset? CompletedFrom { get; init; }

        [FromQuery(Name = "completedTo")]
        public DateTimeOffset? CompletedTo { get; init; }

        [FromQuery(Name = "minDurationMilliseconds")]
        public long? MinDurationMilliseconds { get; init; }

        [FromQuery(Name = "maxDurationMilliseconds")]
        public long? MaxDurationMilliseconds { get; init; }

        [FromQuery(Name = "var")]
        public string[]? Variables { get; init; }

        [FromQuery(Name = "sort")]
        public string[]? Sort { get; init; }

        [FromQuery(Name = "page")]
        public int? Page { get; init; }

        [FromQuery(Name = "pageSize")]
        public int? PageSize { get; init; }
    }
}
