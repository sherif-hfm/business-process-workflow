using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Api.Endpoints;

public static class UserTaskEndpoints
{
    public static IEndpointRouteBuilder MapUserTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/user-tasks").WithTags("User Tasks").RequireAuthorization();
        group.MapGet("/manage", ListManageableTasks)
            .Produces<PagedResult<ManagedUserTaskDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);
        group.MapGet("/{taskId:long}", GetTask).Produces<UserTaskDto>().Produces(StatusCodes.Status404NotFound);
        group.MapGet("/{taskId:long}/flows", GetFlows).Produces<IReadOnlyList<SequenceFlowModel>>();
        group.MapPost("/{taskId:long}/claim", Claim).Produces<UserTaskDto>().Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{taskId:long}/unclaim", Unclaim).Produces<UserTaskDto>();
        group.MapPost("/{taskId:long}/assign", Assign)
            .Produces<UserTaskAssignmentAckDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{taskId:long}/unassign", Unassign)
            .Produces<UserTaskAssignmentAckDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{taskId:long}/flows/{flowId:int}", TakeFlow)
            .Produces<UserTaskActionAckDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<IResult> ListManageableTasks(
        long? taskId,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        string? owner,
        string? ownership,
        [FromQuery(Name = "var")] string[]? variables,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var normalizedPage = Math.Max(1, page ?? 1);
        var normalizedPageSize = Math.Clamp(pageSize ?? 50, 1, 200);
        return Results.Ok(await service.ListManageableUserTasksAsync(
            actorResolver.Resolve(principal),
            taskId,
            instanceId,
            workflowId,
            workflowKey,
            businessKey,
            nodeId,
            nodeExternalId,
            owner,
            ownership,
            variables,
            normalizedPage,
            normalizedPageSize,
            cancellationToken));
    }

    private static async Task<IResult> GetTask(long taskId, ClaimsPrincipal principal,
        IActorContextResolver actorResolver, IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.GetUserTaskAsync(taskId, actorResolver.Resolve(principal), cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> GetFlows(long taskId, ClaimsPrincipal principal,
        IActorContextResolver actorResolver, IWorkflowEngineService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetUserTaskAvailableFlowsAsync(taskId, actorResolver.Resolve(principal), cancellationToken));

    private static async Task<IResult> Claim(long taskId, ClaimsPrincipal principal,
        IActorContextResolver actorResolver, IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.ClaimUserTaskAsync(taskId, actorResolver.Resolve(principal), cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> Unclaim(long taskId, ClaimsPrincipal principal,
        IActorContextResolver actorResolver, IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.UnclaimUserTaskAsync(taskId, actorResolver.Resolve(principal), cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> Assign(
        long taskId,
        AssignUserTaskRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var dto = await service.AssignUserTaskAsync(
            taskId,
            request.ActorId,
            request.ExpectedUpdatedAt,
            request.Reason,
            actorResolver.Resolve(principal),
            cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> Unassign(
        long taskId,
        UnassignUserTaskRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var dto = await service.UnassignUserTaskAsync(
            taskId,
            request.ExpectedUpdatedAt,
            request.Reason,
            actorResolver.Resolve(principal),
            cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> TakeFlow(long taskId, int flowId, TakeFlowRequest request,
        ClaimsPrincipal principal, IActorContextResolver actorResolver,
        IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.TakeUserTaskFlowAsync(
            taskId, flowId, actorResolver.Resolve(principal), request.Variables, cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }
}
