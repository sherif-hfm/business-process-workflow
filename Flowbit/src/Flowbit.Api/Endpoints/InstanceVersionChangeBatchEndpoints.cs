using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class InstanceVersionChangeBatchEndpoints
{
    private const long MaxBatchRequestBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapInstanceVersionChangeBatchEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/instance-version-change-batches")
            .WithTags("Instance Version Change Batches")
            .RequireAuthorization()
            .RequireWorkflowAdministrator();

        group.MapPost("/candidates/search", SearchCandidates)
            .Accepts<InstanceVersionChangeCandidateSearchRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBatchRequestBodyBytes))
            .WithSummary("Search running instances on one exact source workflow version")
            .Produces<PagedResult<InstanceVersionChangeCandidateDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPost(string.Empty, CreateBatch)
            .Accepts<CreateInstanceVersionChangeBatchRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxBatchRequestBodyBytes))
            .WithSummary("Freeze and asynchronously prepare an instance version-change batch")
            .Produces<InstanceVersionChangeBatchDetailDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);

        group.MapGet(string.Empty, ListBatches)
            .Produces<PagedResult<InstanceVersionChangeBatchSummaryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{batchId:long}", GetBatch)
            .Produces<InstanceVersionChangeBatchDetailDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{batchId:long}/items", ListBatchItems)
            .Produces<PagedResult<InstanceVersionChangeBatchItemDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{batchId:long}/confirm", ConfirmBatch)
            .WithSummary("Confirm the displayed compatibility result and queue independent execution")
            .Produces<InstanceVersionChangeBatchDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{batchId:long}/cancel", CancelBatch)
            .WithSummary("Cancel unstarted version changes without reversing successful items")
            .Produces<InstanceVersionChangeBatchDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> SearchCandidates(
        InstanceVersionChangeCandidateSearchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVersionChangeBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.SearchCandidatesAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> CreateBatch(
        CreateInstanceVersionChangeBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVersionChangeBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Accepted(
            $"/api/instance-version-change-batches/{result.Summary.Id}",
            result);
    }

    private static async Task<IResult> ListBatches(
        string? workflowKey,
        long? sourceWorkflowId,
        long? targetWorkflowId,
        string? status,
        string? preparedBy,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVersionChangeBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(
            new InstanceVersionChangeBatchSearchRequest
            {
                WorkflowKey = workflowKey,
                SourceWorkflowId = sourceWorkflowId,
                TargetWorkflowId = targetWorkflowId,
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
        IInstanceVersionChangeBatchService service,
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
        IInstanceVersionChangeBatchService service,
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
        ConfirmInstanceVersionChangeBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVersionChangeBatchService service,
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
        CancelInstanceVersionChangeBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVersionChangeBatchService service,
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
