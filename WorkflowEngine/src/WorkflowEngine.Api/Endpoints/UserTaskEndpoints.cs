using System.Security.Claims;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Shared.Dtos;
using WorkflowEngine.Shared.Models;

namespace WorkflowEngine.Api.Endpoints;

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
        IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.GetUserTaskAsync(taskId, ToActor(principal), cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> GetFlows(long taskId, ClaimsPrincipal principal,
        IWorkflowEngineService service, CancellationToken cancellationToken) =>
        Results.Ok(await service.GetUserTaskAvailableFlowsAsync(taskId, ToActor(principal), cancellationToken));

    private static async Task<IResult> Claim(long taskId, ClaimsPrincipal principal,
        IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.ClaimUserTaskAsync(taskId, ToActor(principal), cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> Unclaim(long taskId, ClaimsPrincipal principal,
        IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.UnclaimUserTaskAsync(taskId, ToActor(principal), cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
    }

    private static async Task<IResult> TakeFlow(long taskId, int flowId, TakeFlowRequest request,
        ClaimsPrincipal principal, IWorkflowEngineService service, CancellationToken cancellationToken)
    {
        var dto = await service.TakeUserTaskFlowAsync(taskId, flowId, ToActor(principal), request.Variables, cancellationToken);
        return dto is null ? Results.NotFound() : Results.Ok(dto);
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
