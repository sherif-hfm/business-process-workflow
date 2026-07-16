using System.Security.Claims;
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
        group.MapGet("/{taskId:long}", GetTask).Produces<UserTaskDto>().Produces(StatusCodes.Status404NotFound);
        group.MapGet("/{taskId:long}/flows", GetFlows).Produces<IReadOnlyList<SequenceFlowModel>>();
        group.MapPost("/{taskId:long}/claim", Claim).Produces<UserTaskDto>().Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{taskId:long}/unclaim", Unclaim).Produces<UserTaskDto>();
        group.MapPost("/{taskId:long}/flows/{flowId:int}", TakeFlow)
            .Produces<UserTaskActionAckDto>().Produces(StatusCodes.Status409Conflict);
        return app;
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

    private static async Task<IResult> TakeFlow(long taskId, int flowId, TakeFlowRequest request,
        ClaimsPrincipal principal, IActorContextResolver actorResolver,
        IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.TakeUserTaskFlowAsync(
            taskId, flowId, actorResolver.Resolve(principal), request.Variables, cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }
}
