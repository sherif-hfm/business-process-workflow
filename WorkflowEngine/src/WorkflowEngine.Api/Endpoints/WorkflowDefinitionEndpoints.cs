using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Shared.Dtos;

namespace WorkflowEngine.Api.Endpoints;

public static class WorkflowDefinitionEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowDefinitionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workflows").WithTags("Workflow Definitions");

        group.MapGet("/", async (
            IWorkflowDefinitionService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.ListLatestAsync(cancellationToken)));

        group.MapGet("/{id:long}", async (
            long id,
            IWorkflowDefinitionService service,
            CancellationToken cancellationToken) =>
        {
            var workflow = await service.GetAsync(id, cancellationToken);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        group.MapPost("/", async (
            CreateWorkflowRequest request,
            IWorkflowDefinitionService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateAsync(request.Definition, request.Publish, cancellationToken);
            return Results.Created($"/api/workflows/{created.Id}", created);
        });

        group.MapPut("/{id:long}", async (
            long id,
            UpdateWorkflowRequest request,
            IWorkflowDefinitionService service,
            CancellationToken cancellationToken) =>
        {
            var created = await service.CreateNewVersionAsync(
                id,
                request.Definition,
                request.Publish,
                cancellationToken);
            return created is null ? Results.NotFound() : Results.Ok(created);
        });

        group.MapPost("/{id:long}/publish", async (
            long id,
            IWorkflowDefinitionService service,
            CancellationToken cancellationToken) =>
            await service.PublishAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        group.MapDelete("/{id:long}", async (
            long id,
            IWorkflowDefinitionService service,
            CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        return app;
    }
}
