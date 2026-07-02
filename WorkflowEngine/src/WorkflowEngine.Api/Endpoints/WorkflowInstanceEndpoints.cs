using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Shared.Dtos;

namespace WorkflowEngine.Api.Endpoints;

public static class WorkflowInstanceEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowInstanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/instances").WithTags("Workflow Instances");

        group.MapPost("/", async (
            StartInstanceRequest request,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.StartInstanceAsync(
                request.WorkflowId,
                request.StartedBy,
                request.Variables,
                cancellationToken);
            return Results.Created($"/api/instances/{instance.Id}", instance);
        });

        group.MapGet("/", async (
            string? status,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListInstancesAsync(status, cancellationToken)));

        group.MapGet("/inbox", async (
            string? user,
            string? roles,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetInboxAsync(
                user,
                roles?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
                cancellationToken)));

        group.MapGet("/{id:long}", async (
            long id,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.GetInstanceAsync(id, cancellationToken);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        });

        group.MapGet("/{id:long}/actions", async (
            long id,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAvailableActionsAsync(id, cancellationToken)));

        group.MapPost("/{id:long}/claim", async (
            long id,
            ClaimRequest request,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.ClaimAsync(id, request.User, cancellationToken);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        });

        group.MapPost("/{id:long}/unclaim", async (
            long id,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.UnclaimAsync(id, cancellationToken);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        });

        group.MapPost("/{id:long}/actions/{actionId:int}", async (
            long id,
            int actionId,
            TakeActionRequest request,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.TakeActionAsync(
                id,
                actionId,
                request.PerformedBy,
                request.Variables,
                cancellationToken);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        });

        group.MapPost("/{id:long}/cancel", async (
            long id,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
            await service.CancelAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
