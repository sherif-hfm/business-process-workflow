using Microsoft.AspNetCore.Mvc;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Endpoints;

public static class TaskDistributionEndpoints
{
    public static IEndpointRouteBuilder MapTaskDistributionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/task-distribution/workflows/{workflowKey}/tasks")
            .WithTags("Task Distribution")
            .AllowAnonymous();

        group.MapGet(string.Empty, ListTasks)
            .Produces<PagedResult<ManagedUserTaskDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);
        group.MapPost("/{taskId:long}/assign", Assign)
            .Produces<UserTaskAssignmentAckDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{taskId:long}/unassign", Unassign)
            .Produces<UserTaskAssignmentAckDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        return app;
    }

    private static async Task<IResult> ListTasks(
        string workflowKey,
        [FromHeader(Name = "X-Client-Id")] string? clientId,
        [FromHeader(Name = "X-Client-Secret")] string? clientSecret,
        long? taskId,
        long? instanceId,
        long? workflowId,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        string? owner,
        string? ownership,
        [FromQuery(Name = "var")] string[]? variables,
        bool? includeVariables,
        int? page,
        int? pageSize,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListDistributableUserTasksAsync(
            workflowKey,
            new TaskDistributionCredentials(clientId, clientSecret),
            taskId,
            instanceId,
            workflowId,
            businessKey,
            nodeId,
            nodeExternalId,
            owner,
            ownership,
            variables,
            includeVariables ?? false,
            Math.Max(1, page ?? 1),
            Math.Clamp(pageSize ?? 50, 1, 200),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Assign(
        string workflowKey,
        long taskId,
        AssignUserTaskRequest request,
        [FromHeader(Name = "X-Client-Id")] string? clientId,
        [FromHeader(Name = "X-Client-Secret")] string? clientSecret,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var result = await service.AssignDistributedUserTaskAsync(
            workflowKey,
            taskId,
            request.ActorId,
            request.ExpectedUpdatedAt,
            request.Reason,
            new TaskDistributionCredentials(clientId, clientSecret),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> Unassign(
        string workflowKey,
        long taskId,
        UnassignUserTaskRequest request,
        [FromHeader(Name = "X-Client-Id")] string? clientId,
        [FromHeader(Name = "X-Client-Secret")] string? clientSecret,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var result = await service.UnassignDistributedUserTaskAsync(
            workflowKey,
            taskId,
            request.ExpectedUpdatedAt,
            request.Reason,
            new TaskDistributionCredentials(clientId, clientSecret),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
