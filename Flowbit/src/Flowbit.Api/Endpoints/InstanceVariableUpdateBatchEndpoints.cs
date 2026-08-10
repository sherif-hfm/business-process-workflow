using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class InstanceVariableUpdateBatchEndpoints
{
    private const long MaxRequestBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapInstanceVariableUpdateBatchEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/instance-variable-update-batches")
            .WithTags("Instance Variable Update Batches")
            .RequireAuthorization()
            .RequireWorkflowAdministrator();

        group.MapPost("/candidates/search", SearchCandidates)
            .Accepts<InstanceVariableUpdateCandidateSearchRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Search running variable-update candidates in one workflow family")
            .Produces<PagedResult<InstanceVariableUpdateCandidateDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost(string.Empty, CreateBatch)
            .Accepts<CreateInstanceVariableUpdateBatchRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Freeze and asynchronously prepare a variable-update batch")
            .Produces<InstanceVariableUpdateBatchDetailDto>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapGet(string.Empty, ListBatches)
            .Produces<PagedResult<InstanceVariableUpdateBatchSummaryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{batchId:long}", GetBatch)
            .Produces<InstanceVariableUpdateBatchDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{batchId:long}/items", ListItems)
            .Produces<PagedResult<InstanceVariableUpdateBatchItemDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{batchId:long}/confirm", ConfirmBatch)
            .Accepts<ConfirmInstanceVariableUpdateBatchRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Confirm the prepared population and queue per-version execution jobs")
            .Produces<InstanceVariableUpdateBatchDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        group.MapPost("/{batchId:long}/cancel", CancelBatch)
            .Accepts<CancelInstanceVariableUpdateBatchRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Cancel unstarted items without reversing successful updates")
            .Produces<InstanceVariableUpdateBatchDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        return app;
    }

    private static async Task<IResult> SearchCandidates(
        InstanceVariableUpdateCandidateSearchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVariableUpdateBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.SearchCandidatesAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> CreateBatch(
        CreateInstanceVariableUpdateBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVariableUpdateBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.CreateAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Accepted(
            $"/api/instance-variable-update-batches/{result.Summary.Id}",
            result);
    }

    private static async Task<IResult> ListBatches(
        string? workflowKey,
        string? status,
        string? preparedBy,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVariableUpdateBatchService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(
            new InstanceVariableUpdateBatchSearchRequest
            {
                WorkflowKey = workflowKey,
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
        IInstanceVariableUpdateBatchService service,
        CancellationToken cancellationToken)
    {
        var result = await service.GetAsync(
            batchId,
            actorResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> ListItems(
        long batchId,
        string? status,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVariableUpdateBatchService service,
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
        ConfirmInstanceVariableUpdateBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVariableUpdateBatchService service,
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
        CancelInstanceVariableUpdateBatchRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVariableUpdateBatchService service,
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
