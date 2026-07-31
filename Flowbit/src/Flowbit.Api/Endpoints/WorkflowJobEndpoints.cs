using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class WorkflowJobEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/jobs")
            .WithTags("Workflow Jobs")
            .RequireAuthorization();

        jobs.MapGet("/", SearchJobs)
            .WithSummary("Search durable workflow jobs")
            .Produces<PagedResult<JobSummaryDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        jobs.MapGet("/statistics", GetQueueStatistics)
            .WithSummary("Get durable workflow queue statistics")
            .Produces<JobQueueStatisticsDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        jobs.MapGet("/{jobId:long}", GetJob)
            .WithSummary("Get durable workflow job metadata")
            .Produces<JobDetailDto>()
            .Produces(StatusCodes.Status404NotFound);
        jobs.MapGet("/{jobId:long}/attempts", ListAttempts)
            .WithSummary("List bounded attempt history for one job")
            .Produces<PagedResult<JobAttemptDto>>();

        var incidents = app.MapGroup("/api/incidents")
            .WithTags("Workflow Incidents")
            .RequireAuthorization();
        incidents.MapGet("/", SearchIncidents)
            .WithSummary("Search workflow incidents")
            .Produces<PagedResult<IncidentSummaryDto>>();
        incidents.MapGet("/{incidentId:long}", GetIncident)
            .WithSummary("Get one workflow incident")
            .Produces<IncidentDetailDto>()
            .Produces(StatusCodes.Status404NotFound);
        incidents.MapPost("/{incidentId:long}/retry", RetryIncident)
            .WithSummary("Resolve an incident by queueing its fenced job for retry")
            .Produces<RetryIncidentResultDto>()
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> SearchJobs(
        [AsParameters] JobHttpQuery query,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowJobOperationsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SearchJobsAsync(
            new WorkflowJobQuery
            {
                InstanceId = query.InstanceId,
                WorkflowDefinitionId = query.WorkflowId,
                WorkflowKey = query.WorkflowKey,
                TokenId = query.TokenId,
                Statuses = query.Statuses ?? [],
                Kinds = query.Kinds ?? [],
                DueFrom = query.DueFrom,
                DueTo = query.DueTo,
                Cursor = query.Cursor,
                Page = Math.Max(1, query.Page ?? 1),
                PageSize = Math.Clamp(query.PageSize ?? 50, 1, 200)
            },
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetQueueStatistics(
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowJobOperationsService service,
        CancellationToken cancellationToken)
    {
        var statistics = await service.GetQueueStatisticsAsync(
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Ok(statistics);
    }

    private static async Task<IResult> GetJob(
        long jobId,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowJobOperationsService service,
        CancellationToken cancellationToken)
    {
        var job = await service.GetJobAsync(
            jobId,
            actorResolver.Resolve(principal),
            cancellationToken);
        return job is null ? Results.NotFound() : Results.Ok(job);
    }

    private static async Task<IResult> ListAttempts(
        long jobId,
        string? cursor,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowJobOperationsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListAttemptsAsync(
            jobId,
            cursor,
            Math.Clamp(pageSize ?? 50, 1, 200),
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> SearchIncidents(
        [AsParameters] IncidentHttpQuery query,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowJobOperationsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.SearchIncidentsAsync(
            new WorkflowIncidentQuery
            {
                InstanceId = query.InstanceId,
                WorkflowDefinitionId = query.WorkflowId,
                WorkflowKey = query.WorkflowKey,
                Statuses = query.Statuses ?? [],
                Types = query.Types ?? [],
                Cursor = query.Cursor,
                Page = Math.Max(1, query.Page ?? 1),
                PageSize = Math.Clamp(query.PageSize ?? 50, 1, 200)
            },
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetIncident(
        long incidentId,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowJobOperationsService service,
        CancellationToken cancellationToken)
    {
        var incident = await service.GetIncidentAsync(
            incidentId,
            actorResolver.Resolve(principal),
            cancellationToken);
        return incident is null ? Results.NotFound() : Results.Ok(incident);
    }

    private static async Task<IResult> RetryIncident(
        long incidentId,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowJobOperationsService service,
        CancellationToken cancellationToken)
    {
        var result = await service.RetryIncidentAsync(
            incidentId,
            actorResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    public sealed class JobHttpQuery
    {
        [FromQuery(Name = "instanceId")] public long? InstanceId { get; init; }
        [FromQuery(Name = "workflowId")] public long? WorkflowId { get; init; }
        [FromQuery(Name = "workflowKey")] public string? WorkflowKey { get; init; }
        [FromQuery(Name = "tokenId")] public long? TokenId { get; init; }
        [FromQuery(Name = "status")] public string[]? Statuses { get; init; }
        [FromQuery(Name = "kind")] public string[]? Kinds { get; init; }
        [FromQuery(Name = "dueFrom")] public DateTimeOffset? DueFrom { get; init; }
        [FromQuery(Name = "dueTo")] public DateTimeOffset? DueTo { get; init; }
        [FromQuery(Name = "cursor")] public string? Cursor { get; init; }
        [FromQuery(Name = "page")] public int? Page { get; init; }
        [FromQuery(Name = "pageSize")] public int? PageSize { get; init; }
    }

    public sealed class IncidentHttpQuery
    {
        [FromQuery(Name = "instanceId")] public long? InstanceId { get; init; }
        [FromQuery(Name = "workflowId")] public long? WorkflowId { get; init; }
        [FromQuery(Name = "workflowKey")] public string? WorkflowKey { get; init; }
        [FromQuery(Name = "status")] public string[]? Statuses { get; init; }
        [FromQuery(Name = "type")] public string[]? Types { get; init; }
        [FromQuery(Name = "cursor")] public string? Cursor { get; init; }
        [FromQuery(Name = "page")] public int? Page { get; init; }
        [FromQuery(Name = "pageSize")] public int? PageSize { get; init; }
    }
}
