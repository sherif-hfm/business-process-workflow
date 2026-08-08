using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class AdministrativeActionEndpoints
{
    private const long MaxBatchRequestBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapAdministrativeActionEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/administrative-actions/workflows",
                ListWorkflowCatalog)
            .WithTags("Administrative Actions")
            .RequireAuthorization()
            .WithSummary("List exact workflow versions containing administrative batch source nodes")
            .Produces<IReadOnlyList<WorkflowSummaryDto>>()
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapGet(
                "/api/workflows/{workflowId:long}/administrative-actions/nodes",
                ListSourceNodes)
            .WithTags("Administrative Actions")
            .RequireAuthorization()
            .WithSummary("List ordinary and multi-instance user-task source nodes in an exact workflow version")
            .Produces<IReadOnlyList<AdministrativeActionSourceNodeDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapGet(
                "/api/workflows/{workflowId:long}/nodes/{sourceNodeId:int}/administrative-actions",
                ListWorkflowActions)
            .WithTags("Administrative Actions")
            .RequireAuthorization()
            .WithSummary("List direct flows and attached timer-boundary actions without normal task authorization filtering")
            .Produces<IReadOnlyList<AdministrativeActionSummaryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        app.MapPost(
                "/api/administrative-actions/candidates/search",
                SearchCandidates)
            .WithTags("Administrative Actions")
            .RequireAuthorization()
            .Accepts<AdministrativeActionCandidateSearchRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBatchRequestBodyBytes))
            .WithSummary("Search active ordinary-task and multi-instance execution positions at an exact node")
            .Produces<PagedResult<AdministrativeActionCandidateDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        var batches = app.MapGroup("/api/administrative-action-batches")
            .WithTags("Administrative Actions")
            .RequireAuthorization();
        batches.MapPost(string.Empty, CreateBatch)
            .Accepts<CreateAdministrativeActionBatchRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBatchRequestBodyBytes))
            .WithSummary("Freeze a selection and asynchronously prepare an administrative-action batch")
            .Produces<AdministrativeActionBatchDetailDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict);
        batches.MapGet(string.Empty, ListBatches)
            .Produces<PagedResult<AdministrativeActionBatchSummaryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);
        batches.MapGet("/{batchId:long}", GetBatch)
            .Produces<AdministrativeActionBatchDetailDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);
        batches.MapGet("/{batchId:long}/items", ListBatchItems)
            .Produces<PagedResult<AdministrativeActionBatchItemDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);
        batches.MapPost("/{batchId:long}/confirm", ConfirmBatch)
            .WithSummary("Idempotently confirm the displayed eligible set and queue independent execution")
            .Produces<AdministrativeActionBatchDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        batches.MapPost("/{batchId:long}/cancel", CancelBatch)
            .WithSummary("Stop unstarted items without reversing successful administrative actions")
            .Produces<AdministrativeActionBatchDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<IResult> ListWorkflowCatalog(
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListWorkflowCatalogAsync(
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> ListWorkflowActions(
        long workflowId,
        int sourceNodeId,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListActionsAsync(
            workflowId,
            sourceNodeId,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> ListSourceNodes(
        long workflowId,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListSourceNodesAsync(
            workflowId,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> SearchCandidates(
        AdministrativeActionCandidateSearchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.SearchCandidatesAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> CreateBatch(
        CreateAdministrativeActionBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Accepted(
            $"/api/administrative-action-batches/{result.Summary.Id}",
            result);
    }

    private static async Task<IResult> ListBatches(
        string? workflowKey,
        long? workflowDefinitionId,
        string? status,
        string? preparedBy,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(
            new AdministrativeActionBatchSearchRequest
            {
                WorkflowKey = workflowKey,
                WorkflowDefinitionId = workflowDefinitionId,
                Status = status,
                PreparedBy = preparedBy,
                Page = page,
                PageSize = pageSize
            },
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> GetBatch(
        long batchId,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(
            batchId,
            actorResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ListBatchItems(
        long batchId,
        string? status,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListItemsAsync(
            batchId,
            status,
            page ?? 1,
            pageSize ?? 50,
            actorResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ConfirmBatch(
        long batchId,
        ConfirmAdministrativeActionBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ConfirmAsync(
            batchId,
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> CancelBatch(
        long batchId,
        CancelAdministrativeActionBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IAdministrativeActionBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CancelAsync(
            batchId,
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
