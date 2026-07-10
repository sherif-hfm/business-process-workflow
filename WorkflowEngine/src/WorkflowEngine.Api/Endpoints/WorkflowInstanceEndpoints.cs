using System.Security.Claims;
using System.Text.Json;
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
            string? detail,
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var actor = ToActor(principal);
            if (string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase))
            {
                var instance = await service.StartInstanceAsync(
                    request.WorkflowId,
                    request.WorkflowKey,
                    actor,
                    request.StartEventId,
                    request.Variables,
                    cancellationToken);
                return Results.Created($"/api/instances/{instance.Id}", instance);
            }

            var result = await service.StartInstanceSlimAsync(
                request.WorkflowId,
                request.WorkflowKey,
                actor,
                request.StartEventId,
                request.Variables,
                cancellationToken);
            return Results.Created($"/api/instances/{result.Id}", result);
        });

        group.MapGet("/", async (
            string? status,
            long? instanceId,
            long? workflowId,
            string? workflowKey,
            int? nodeId,
            string? nodeExternalId,
            [FromQuery(Name = "var")] string[]? variables,
            int? page,
            int? pageSize,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var (p, s) = NormalizePaging(page, pageSize);
            return Results.Ok(await service.ListInstancesAsync(status, instanceId, workflowId, workflowKey, nodeId, nodeExternalId, variables, p, s, cancellationToken));
        });

        group.MapGet("/inbox", async (
            long? instanceId,
            long? workflowId,
            string? workflowKey,
            int? nodeId,
            string? nodeExternalId,
            [FromQuery(Name = "var")] string[]? variables,
            int? page,
            int? pageSize,
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var (p, s) = NormalizePaging(page, pageSize);
            return Results.Ok(await service.GetInboxAsync(ToActor(principal), instanceId, workflowId, workflowKey, nodeId, nodeExternalId, variables, p, s, cancellationToken));
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
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var instance = await service.UnclaimAsync(id, ToActor(principal), cancellationToken);
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
            ClaimsPrincipal principal,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
            await service.CancelAsync(id, ToActor(principal), cancellationToken) ? Results.NoContent() : Results.NotFound());

        // Delivers a message to an instance resting on an intermediateMessageCatchEvent.
        // Auth is the node-config client id/secret + required header (not the user JWT),
        // so this endpoint overrides the group RequireAuthorization() with AllowAnonymous.
        // The body is the raw JSON message payload; outputMappings on the catch node
        // extract values from it. Returns a slim ack (no definition/variables/history)
        // so a node-credentialed webhook caller cannot read the full workflow model:
        // 404 when the instance is missing, 401 on a client id/secret mismatch
        // (WorkflowUnauthorizedException), 400 on a header problem (missing/mismatch/
        // validation failure) or when not running/waiting (WorkflowDomainException).
        // A non-JSON content type is treated as no payload (rather than throwing a 500)
        // so a misconfigured caller gets a clean response.
        group.MapPost("/{id:long}/message", async (
            HttpContext context,
            long id,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();
            var clientSecret = context.Request.Headers["X-Client-Secret"].FirstOrDefault();
            var headers = context.Request.Headers
                .ToDictionary(h => h.Key, h => (string?)h.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);

            JsonElement? payload = null;
            // Only attempt a JSON read when the caller declared a JSON content type;
            // ReadFromJsonAsync throws InvalidOperationException (not JsonException) for a
            // non-JSON content type, which would otherwise surface as an unhandled 500.
            if (context.Request.HasJsonContentType()
                && (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            {
                try
                {
                    payload = await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken);
                }
                catch (JsonException)
                {
                    payload = null;
                }
            }

            var actor = new ActorContext(clientId, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            var message = new IncomingMessage(clientId, clientSecret, headers, payload, actor);
            var ack = await service.DeliverMessageAsync(id, message, cancellationToken);
            return ack is null ? Results.NotFound() : Results.Ok(ack);
        }).AllowAnonymous();

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
