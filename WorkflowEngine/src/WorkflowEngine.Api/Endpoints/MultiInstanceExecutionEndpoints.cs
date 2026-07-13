using System.Security.Claims;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Api.Endpoints;

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
        IWorkflowEngineService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.GetMultiInstanceInterruptFlowsAsync(
            executionId, ToActor(principal), cancellationToken));

    private static async Task<IResult> TakeInterruptFlow(
        long executionId,
        int flowId,
        TakeFlowRequest request,
        ClaimsPrincipal principal,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var instance = await service.TakeMultiInstanceInterruptFlowAsync(
            executionId, flowId, ToActor(principal), request.Variables, cancellationToken);
        return instance is null ? Results.NotFound() : Results.Ok(instance);
    }

    private static ActorContext ToActor(ClaimsPrincipal principal)
    {
        var user = principal.Identity?.Name ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var roles = principal.FindAll(ClaimTypes.Role).Select(c => c.Value).ToArray();
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in principal.Claims) claims.TryAdd(claim.Type, claim.Value);
        return new ActorContext(user, roles, claims);
    }
}
