using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using WorkflowEngine.Service.Abstractions;
using WorkflowEngine.Shared.Dtos;

namespace WorkflowEngine.Api.Endpoints;

public static class WorkflowDefinitionEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowDefinitionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workflows")
            .WithTags("Workflow Definitions")
            .RequireAuthorization();

        group.AddEndpointFilter(async (invocationContext, next) =>
        {
            var httpContext = invocationContext.HttpContext;

            // Skip auth check if the endpoint has AllowAnonymous metadata
            var endpoint = httpContext.GetEndpoint();
            if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            {
                return await next(invocationContext);
            }

            var settingsService = httpContext.RequestServices.GetRequiredService<IEngineSettingsService>();

            // Fetch the required role from engine settings, defaulting to "admin" if not configured
            var setting = await settingsService.GetByKeyAsync("Workflow.RequiredRole", httpContext.RequestAborted);
            var requiredRole = !string.IsNullOrWhiteSpace(setting?.Value) ? setting.Value : "admin";

            var allowedRoles = requiredRole.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // Check if the user is in any of the allowed roles (case-insensitive)
            var userRoles = httpContext.User.FindAll(System.Security.Claims.ClaimTypes.Role)
                .Select(c => c.Value)
                .Concat(httpContext.User.FindAll("role").Select(c => c.Value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!allowedRoles.Any(r => userRoles.Contains(r)))
            {
                Log.Warning("User '{User}' with roles [{Roles}] is forbidden from accessing workflow definitions. Required role(s): '{RequiredRole}'",
                    httpContext.User.Identity?.Name ?? "anonymous",
                    string.Join(", ", userRoles),
                    requiredRole);
                return Results.Forbid();
            }

            return await next(invocationContext);
        });

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

        // Starts a new instance by delivering a message to a messageStartEvent.
        // Auth is the node-config client id/secret + required header (not a user
        // JWT), so this endpoint overrides any group auth with AllowAnonymous. The
        // caller addresses the workflow by its stable cross-version workflowKey;
        // an optional ?startEvent={externalId} selects a specific message-start
        // event when the workflow has more than one. The body is the raw JSON
        // message payload; outputMappings on the start node extract values into the
        // node's declared start variables. Returns a slim ack (no
        // definition/variables/history): 401 on a client id/secret mismatch, 400 on
        // a header problem / required-mapping failure / no published version /
        // ambiguous or absent start event. A retried delivery with the same
        // idempotency key returns the existing instance's ack (no duplicate). A
        // non-JSON content type is treated as no payload (rather than throwing a
        // 500) so a misconfigured caller gets a clean response.
        group.MapPost("/{workflowKey}/message-start", async (
            HttpContext context,
            string workflowKey,
            IWorkflowEngineService service,
            CancellationToken cancellationToken) =>
        {
            var startEventExternalId = context.Request.Query["startEvent"].FirstOrDefault();
            var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();
            var clientSecret = context.Request.Headers["X-Client-Secret"].FirstOrDefault();
            var headers = context.Request.Headers
                .ToDictionary(h => h.Key, h => (string?)h.Value.FirstOrDefault(), StringComparer.OrdinalIgnoreCase);

            Log.Information("Message-start request for workflowKey {WorkflowKey} from client '{ClientId}' (startEvent={StartEvent})",
                workflowKey, clientId, startEventExternalId ?? "(default)");

            JsonElement? payload = null;
            if (context.Request.HasJsonContentType()
                && (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")))
            {
                try
                {
                    payload = await context.Request.ReadFromJsonAsync<JsonElement>(cancellationToken);
                }
                catch (JsonException ex)
                {
                    Log.Warning(ex, "Failed to parse incoming JSON payload on message-start endpoint for workflowKey {WorkflowKey}.", workflowKey);
                    payload = null;
                }
            }

            var actor = new ActorContext(clientId, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
            var message = new IncomingMessage(clientId, clientSecret, headers, payload, actor);
            var ack = await service.StartByMessageAsync(workflowKey, startEventExternalId, message, cancellationToken);
            Log.Information("Message-start for workflowKey {WorkflowKey} acknowledged. Instance: {InstanceId}, Status: {Status}, resting on node {NodeId}.",
                workflowKey, ack.InstanceId, ack.Status, ack.CurrentNodeId);
            return Results.Ok(ack);
        }).AllowAnonymous();

        return app;
    }
}
