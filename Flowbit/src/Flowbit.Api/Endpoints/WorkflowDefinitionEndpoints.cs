using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Api.Endpoints;

/// <summary>
/// Exposes API endpoints for managing workflow definitions.
/// </summary>
public static class WorkflowDefinitionEndpoints
{
    /// <summary>
    /// Maps the workflow definition endpoints to the application's route builder.
    /// </summary>
    /// <param name="app">The route builder to map endpoints onto.</param>
    /// <returns>The modified endpoint route builder.</returns>
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

            var actorResolver = httpContext.RequestServices.GetRequiredService<IActorContextResolver>();
            var actor = actorResolver.Resolve(httpContext.User);
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
                    actor.User ?? "anonymous",
                    string.Join(", ", userRoles),
                    requiredRole);
                return Results.Forbid();
            }

            return await next(invocationContext);
        });

        group.MapGet("/", GetLatestWorkflows)
            .Produces<IReadOnlyList<WorkflowSummaryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{workflowKey}/versions", GetWorkflowVersions)
            .Produces<IReadOnlyList<WorkflowSummaryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapGet("/{id:long}", GetWorkflowById)
            .Produces<WorkflowDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/", CreateWorkflow)
            .Produces<WorkflowDetailDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        group.MapPut("/{id:long}", CreateWorkflowNewVersion)
            .Produces<WorkflowDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:long}/publish", PublishWorkflow)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:long}/unpublish", UnpublishWorkflow)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:long}/set-default", SetDefaultWorkflow)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapDelete("/{id:long}", DeleteWorkflow)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{workflowKey}/message-start", StartWorkflowByMessage)
            .AllowAnonymous()
            .Produces<MessageStartAckDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces<StartConflictDto>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    /// <summary>
    /// Lists the latest versions of all workflow definitions.
    /// </summary>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Returns one summary per workflow definition - the most recent version of each
    /// (regardless of publish state). Use a specific version id with
    /// <c>GET /api/workflows/{id}</c> to fetch the full definition JSON.
    /// </remarks>
    public static async Task<IResult> GetLatestWorkflows(
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await service.ListLatestAsync(cancellationToken));
    }

    /// <summary>
    /// Retrieves a specific workflow definition version by its database ID.
    /// </summary>
    /// <param name="id">The database ID of the workflow definition version.</param>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Each version gets its own database id, so this returns the exact version requested,
    /// not necessarily the latest. Use the returned <c>WorkflowKey</c> to correlate across
    /// versions of the same workflow.
    /// </remarks>
    public static async Task<IResult> GetWorkflowById(
        long id,
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        var workflow = await service.GetAsync(id, cancellationToken);
        return workflow is null ? Results.NotFound() : Results.Ok(workflow);
    }

    /// <summary>
    /// Creates a new workflow definition (Version 1).
    /// </summary>
    /// <param name="request">The definition details and whether to publish immediately.</param>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The <c>Definition</c> is the workflow JSON model produced by the visual editor
    /// (see <c>workflow.json</c> for an example). Validates the definition before
    /// storing. Set <c>Publish</c> to make it immediately startable. A 400 is returned
    /// when the definition fails validation (missing entry, dangling sequence flow
    /// references, invalid NCalc expressions, etc.).
    /// </remarks>
    public static async Task<IResult> CreateWorkflow(
        CreateWorkflowRequest request,
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(request.Definition, request.Publish, cancellationToken);
        return Results.Created($"/api/workflows/{created.Id}", created);
    }

    /// <summary>
    /// Creates a new version of an existing workflow definition.
    /// </summary>
    /// <param name="id">The database ID of the workflow version to base the update on.</param>
    /// <param name="request">The updated workflow structure and whether to publish it.</param>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The new version shares the <c>WorkflowKey</c> (stamped from the JSON model id) of
    /// the source version. A 404 is returned when <paramref name="id"/> does not match an
    /// existing version; a 400 is returned when the updated definition fails validation.
    /// </remarks>
    public static async Task<IResult> CreateWorkflowNewVersion(
        long id,
        UpdateWorkflowRequest request,
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        var created = await service.CreateNewVersionAsync(
            id,
            request.Definition,
            request.Publish,
            cancellationToken);
        return created is null ? Results.NotFound() : Results.Ok(created);
    }

    /// <summary>
    /// Lists all versions of a workflow definition identified by its workflow key.
    /// </summary>
    /// <param name="workflowKey">The stable cross-version key identifying the workflow.</param>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Returns all version rows sharing the given <c>WorkflowKey</c>, ordered by version
    /// descending. Each row includes <c>IsPublished</c> and <c>IsDefault</c> flags so the
    /// caller can see which version is active for starting instances and which is the default.
    /// </remarks>
    public static async Task<IResult> GetWorkflowVersions(
        string workflowKey,
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        var versions = await service.ListVersionsAsync(workflowKey, cancellationToken);
        return Results.Ok(versions);
    }

    /// <summary>
    /// Publishes a specific version of a workflow definition, making it available for starting new instances.
    /// </summary>
    /// <param name="id">The database ID of the workflow version to publish.</param>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Publishing a version makes it available for starting instances. Multiple versions
    /// of the same workflow key can be published simultaneously; the default version
    /// (set via <c>POST /api/workflows/{id}/set-default</c>) is the one used when starting
    /// by <c>WorkflowKey</c>. Returns 204 on success or 404 when the version does not exist.
    /// </remarks>
    public static async Task<IResult> PublishWorkflow(
        long id,
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        return await service.PublishAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Unpublishes a specific version of a workflow definition, making it unavailable for starting new instances.
    /// </summary>
    /// <param name="id">The database ID of the workflow version to unpublish.</param>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Unpublishing removes the version from the set of versions available for starting
    /// instances. Running instances are not affected. The current default version cannot
    /// be unpublished; set a different version as default first. Returns 204 on success,
    /// 404 when the version does not exist, or 400 when attempting to unpublish the default.
    /// </remarks>
    public static async Task<IResult> UnpublishWorkflow(
        long id,
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        return await service.UnpublishAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Sets a specific version as the default for its workflow key.
    /// </summary>
    /// <param name="id">The database ID of the workflow version to set as default.</param>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// The default version is the one used when starting instances by <c>WorkflowKey</c>
    /// (including message-start events). Only one version per <c>WorkflowKey</c> can be
    /// the default at a time; setting a new default clears the previous one. The target
    /// version must be published; attempting to set an unpublished version as default
    /// returns 400. Returns 204 on success or 404 when the version does not exist.
    /// </remarks>
    public static async Task<IResult> SetDefaultWorkflow(
        long id,
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        return await service.SetDefaultAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Deletes a specific version of a workflow definition.
    /// </summary>
    /// <param name="id">The database ID of the workflow version to delete.</param>
    /// <param name="service">The workflow definition service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Deletes a single version row. Instances already running against this version are
    /// not affected (the definition snapshot is retained on the instance). Returns 204
    /// on success or 404 when the version does not exist.
    /// </remarks>
    public static async Task<IResult> DeleteWorkflow(
        long id,
        IWorkflowDefinitionService service,
        CancellationToken cancellationToken)
    {
        return await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Starts a new workflow instance by delivering an initial message payload to a system-only message start event.
    /// </summary>
    /// <param name="context">The HTTP request context containing custom correlation headers.</param>
    /// <param name="workflowKey">The stable cross-version key identifying the workflow.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// This endpoint is <c>AllowAnonymous</c> - authentication is the message-start node's
    /// configured client id/secret (sent as <c>X-Client-Id</c>/<c>X-Client-Secret</c>) plus a
    /// required custom header named by the node's <c>headerName</c>. The default
    /// version of the workflow is resolved by <paramref name="workflowKey"/>. The message
    /// <c>outputMappings</c> are both payload mappings and typed start-variable declarations.
    /// Values are strictly type-checked; a missing path may use its mapping default, required
    /// final values are enforced, and NCalc <c>validation</c> rules run against the complete
    /// resolved value set. When the selected entry configures node-level
    /// <c>idempotency</c>, it declares a separate implicit required string populated only
    /// from its configured HTTP header. The trimmed value is permanently unique for the
    /// stable workflow key across versions and both start routes; a retry returns 409 with
    /// <c>idempotency_conflict</c>, the existing instance id, and a <c>Location</c> header.
    ///
    /// Returns a slim <see cref="MessageStartAckDto"/> (no definition/variables/history, since
    /// the endpoint is anonymous). A 401 is returned on a client id/secret mismatch; a 400
    /// is returned for a missing/mismatched header, a failed <c>headerValidation</c> rule, a
    /// required-mapping failure, no default version, or an ambiguous/absent start event. A
    /// duplicate idempotency key or business key returns a slim
    /// <see cref="StartConflictDto"/> with 409 and a <c>Location</c> header for the
    /// existing instance.
    /// </remarks>
    public static async Task<IResult> StartWorkflowByMessage(
        HttpContext context,
        string workflowKey,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var startEventExternalId = context.Request.Query["startEvent"].FirstOrDefault();
        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();
        var clientSecret = context.Request.Headers["X-Client-Secret"].FirstOrDefault();
        var headers = context.Request.Headers
            .ToDictionary(
                h => h.Key,
                h => (IReadOnlyList<string>)h.Value.Select(value => value ?? string.Empty).ToArray(),
                StringComparer.OrdinalIgnoreCase);

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
        try
        {
            var ack = await service.StartByMessageAsync(workflowKey, startEventExternalId, message, cancellationToken);
            Log.Information("Message-start for workflowKey {WorkflowKey} acknowledged. Instance: {InstanceId}, Status: {Status}, resting on node {NodeId}.",
                workflowKey, ack.InstanceId, ack.Status, ack.CurrentNodeId);
            return Results.Ok(ack);
        }
        catch (IdempotencyKeyConflictException ex)
        {
            return MessageStartConflict(context, "idempotency_conflict", ex.ExistingInstanceId);
        }
        catch (BusinessKeyConflictException ex)
        {
            return MessageStartConflict(context, "business_key_conflict", ex.ExistingInstanceId);
        }
    }

    private static IResult MessageStartConflict(
        HttpContext context,
        string code,
        long instanceId)
    {
        Log.Information("Message-start conflict {ConflictCode}. Existing instance: {InstanceId}.", code, instanceId);
        context.Response.Headers.Location = $"/api/instances/{instanceId}";
        return Results.Json(
            new StartConflictDto(code, instanceId),
            statusCode: StatusCodes.Status409Conflict);
    }
}
