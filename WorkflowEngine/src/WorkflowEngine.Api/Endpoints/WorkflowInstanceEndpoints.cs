using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Shared.Dtos;

namespace WorkflowEngine.Api.Endpoints;

public static class WorkflowInstanceEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowInstanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/instances")
            .WithTags("Workflow Instances")
            .RequireAuthorization();

        group.MapPost("/", async (
            StartInstanceRequest request,
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.StartInstanceAsync(
                request.WorkflowId,
                ToActor(principal),
                request.StartEventId,
                request.Variables,
                cancellationToken);
            return Results.Created($"/api/instances/{instance.Id}", instance);
        });

        group.MapGet("/", async (
            string? status,
            [FromQuery(Name = "var")] string[]? variables,
            int? page,
            int? pageSize,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var (p, s) = NormalizePaging(page, pageSize);
            return Results.Ok(await service.ListInstancesAsync(status, variables, p, s, cancellationToken));
        });

        group.MapGet("/inbox", async (
            [FromQuery(Name = "var")] string[]? variables,
            int? page,
            int? pageSize,
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var (p, s) = NormalizePaging(page, pageSize);
            return Results.Ok(await service.GetInboxAsync(ToActor(principal), variables, p, s, cancellationToken));
        });

        group.MapGet("/{id:long}", async (
            long id,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.GetInstanceAsync(id, cancellationToken);
            return instance is null ? Results.NotFound() : Results.Ok(instance);
        });

        group.MapGet("/{id:long}/flows", async (
            long id,
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAvailableFlowsAsync(id, ToActor(principal), cancellationToken)));

        group.MapPost("/{id:long}/claim", async (
            long id,
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.ClaimAsync(id, ToActor(principal), cancellationToken);
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

        group.MapPost("/{id:long}/flows/{flowId:int}", async (
            long id,
            int flowId,
            TakeFlowRequest request,
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.TakeFlowAsync(
                id,
                flowId,
                ToActor(principal),
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

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static (int Page, int PageSize) NormalizePaging(int? page, int? pageSize)
    {
        var normalizedPage = page is > 0 ? page.Value : 1;
        var normalizedPageSize = pageSize switch
        {
            null or <= 0 => DefaultPageSize,
            > MaxPageSize => MaxPageSize,
            _ => pageSize.Value
        };
        return (normalizedPage, normalizedPageSize);
    }

    private static ActorContext ToActor(ClaimsPrincipal principal)
    {
        var user = principal.Identity?.Name
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var roles = principal.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .ToArray();

        // Capture all claims (first value per type); the engine applies the
        // configured allowlist when exposing them as sys.claim.* context values.
        var claims = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var claim in principal.Claims)
        {
            claims.TryAdd(claim.Type, claim.Value);
        }

        return new ActorContext(user, roles, claims);
    }
}
