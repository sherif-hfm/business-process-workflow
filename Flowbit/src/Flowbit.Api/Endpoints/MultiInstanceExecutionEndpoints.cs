using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Api.Endpoints;

public static class MultiInstanceExecutionEndpoints
{
    public static IEndpointRouteBuilder MapMultiInstanceExecutionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/multi-instance-executions")
            .WithTags("Multi-Instance Executions")
            .RequireAuthorization();

        group.MapGet("/{executionId:long}/flows", GetInterruptFlows)
            .Produces<IReadOnlyList<SequenceFlowModel>>()
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/{executionId:long}/flows/{flowId:int}", TakeInterruptFlow)
            .Produces<InstanceDetailDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> GetInterruptFlows(
        long executionId,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetMultiInstanceInterruptFlowsAsync(
            executionId, actorResolver.Resolve(principal), cancellationToken));

    private static async Task<IResult> TakeInterruptFlow(
        long executionId,
        int flowId,
        TakeFlowRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var instance = await service.TakeMultiInstanceInterruptFlowAsync(
            executionId, flowId, actorResolver.Resolve(principal), request.Variables, cancellationToken);
        return instance is null ? Results.NotFound() : Results.Ok(instance);
    }
}
