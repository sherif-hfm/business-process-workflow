using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Serilog;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Api.Endpoints;

/// <summary>
/// Exposes API endpoints for managing workflow instances.
/// </summary>
public static class WorkflowInstanceEndpoints
{
    /// <summary>
    /// Maps the workflow instance endpoints to the application's route builder.
    /// </summary>
    /// <param name="app">The route builder to map endpoints onto.</param>
    /// <returns>The modified endpoint route builder.</returns>
    public static IEndpointRouteBuilder MapWorkflowInstanceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/instances")
            .WithTags("Workflow Instances")
            .RequireAuthorization();

        group.MapPost("/", StartInstance)
            .Produces<StartInstanceResultDto>(StatusCodes.Status201Created)
            .Produces<InstanceDetailDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces<StartConflictDto>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/", ListInstances)
            .Produces<PagedResult<InstanceSummaryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/inbox", GetInbox)
            .Produces<PagedResult<InboxItemDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapGet("/{id:long}", GetInstance)
            .Produces<InstanceDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/{id:long}/user-tasks", ListUserTasks)
            .Produces<PagedResult<UserTaskDto>>(StatusCodes.Status200OK);

        group.MapGet("/{id:long}/flows", GetAvailableFlows)
            .Produces<IReadOnlyList<SequenceFlowModel>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        group.MapPost("/{id:long}/claim", ClaimInstance)
            .Produces<InstanceDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:long}/unclaim", UnclaimInstance)
            .Produces<InstanceDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound);

        group.MapPost("/{id:long}/flows/{flowId:int}", TakeFlow)
            .Produces<InstanceDetailDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:long}/cancel", CancelInstance)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapPost("/{id:long}/message", DeliverMessage)
            .AllowAnonymous()
            .Accepts<JsonElement>("application/json")
            .Produces<MessageDeliveryAckDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status404NotFound)
            .Produces<MessageDeliveryConflictDto>(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        return app;
    }

    /// <summary>
    /// Starts a new workflow instance based on a workflow definition ID or key.
    /// </summary>
    /// <param name="context">HTTP context containing the configured idempotency header.</param>
    /// <param name="request">Parameters specifying the workflow to start and initial variables.</param>
    /// <param name="detail">Optional. If set to 'full', returns the detailed instance DTO instead of the slim version.</param>
    /// <param name="principal">The security principal containing the actor identity.</param>
    /// <param name="actorResolver">Resolves the configured canonical actor identity from the principal.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Provide either <c>WorkflowId</c> (a specific version database id) or
    /// <c>WorkflowKey</c> (the stable cross-version key - its published default version is
    /// resolved). Exactly one selector is required. <c>StartEventId</c> is optional and selects a non-default start event when
    /// the workflow defines more than one. <c>Variables</c> supplies the start event's
    /// declared variables; required variables must be present, supplied JSON values must
    /// match their declared scalar/array types, and each is validated against its NCalc
    /// <c>validation</c> rule.
    /// When the selected entry configures <c>idempotency</c>, its implicit string value must
    /// be supplied only through the configured HTTP header. The trimmed value is permanently
    /// unique for the stable workflow key across versions and start routes. A duplicate
    /// returns 409 with <see cref="StartConflictDto"/> and a <c>Location</c> header.
    ///
    /// By default the response is the slim <see cref="StartInstanceResultDto"/> (instance id
    /// + resting node); pass <paramref name="detail"/>=<c>"full"</c> to get the full
    /// <see cref="InstanceDetailDto"/> (definition, variables, history) instead. The instance
    /// is created and pass-through routing runs in the same transaction, so the resting node
    /// reflects any automatic nodes (task/serviceTask/scriptTask/exclusiveGateway) between
    /// the start event and the first userTask / message catch / end event.
    ///
    /// A 400 is returned for an unpublished/missing workflow, an invalid/missing/repeated
    /// configured idempotency header, a missing required variable, a failed validation rule,
    /// or a start-event role mismatch (all domain errors). A 401 is returned when no bearer
    /// JWT is supplied.
    /// </remarks>
    public static async Task<IResult> StartInstance(
        HttpContext context,
        StartInstanceRequest request,
        string? detail,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var actor = actorResolver.Resolve(principal);
        var headers = ToHeaderDictionary(context.Request.Headers);
        try
        {
            if (string.Equals(detail, "full", StringComparison.OrdinalIgnoreCase))
            {
                var instance = await service.StartInstanceAsync(
                    request.WorkflowId,
                    request.WorkflowKey,
                    actor,
                    request.StartEventId,
                    request.Variables,
                    headers,
                    cancellationToken);
                return Results.Created($"/api/instances/{instance.Id}", instance);
            }

            var result = await service.StartInstanceSlimAsync(
                request.WorkflowId,
                request.WorkflowKey,
                actor,
                request.StartEventId,
                request.Variables,
                headers,
                cancellationToken);
            return Results.Created($"/api/instances/{result.Id}", result);
        }
        catch (IdempotencyKeyConflictException ex)
        {
            context.Response.Headers.Location = $"/api/instances/{ex.ExistingInstanceId}";
            return Results.Json(
                new StartConflictDto("idempotency_conflict", ex.ExistingInstanceId),
                statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>
    /// Lists workflow instances matching filter criteria.
    /// </summary>
    /// <param name="status">Optional. Filter by execution status (<c>running</c>, <c>completed</c>, <c>faulted</c>, <c>cancelled</c>).</param>
    /// <param name="instanceId">Optional. Filter by unique instance ID.</param>
    /// <param name="workflowId">Optional. Filter by specific workflow version ID.</param>
    /// <param name="workflowKey">Optional. Filter by stable cross-version workflow key (spans all versions).</param>
    /// <param name="businessKey">Optional. Exact, case-sensitive business-key match after trimming.</param>
    /// <param name="nodeId">Optional. Filter by the ID of the current resting flow node.</param>
    /// <param name="nodeExternalId">Optional. Filter by the external ID of the current resting flow node (case-insensitive).</param>
    /// <param name="variables">Optional. Repeated <c>var=name:value</c> filters; exact case-insensitive match on an instance variable's latest scalar value, AND-combined.</param>
    /// <param name="includeVariables">Optional. Include the latest value of every instance variable in each summary (default false).</param>
    /// <param name="page">Optional. The 1-based page index (default 1).</param>
    /// <param name="pageSize">Optional. The number of items per page (default 50, max 200).</param>
    /// <param name="principal">The security principal containing the actor identity.</param>
    /// <param name="actorResolver">Validates the configured canonical actor identity.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Returns a <see cref="PagedResult{T}"/> of <see cref="InstanceSummaryDto"/> ordered by
    /// <c>UpdatedAt DESC, Id DESC</c>. All filters AND-combine. A 400 is returned for a
    /// malformed <c>var</c> entry (missing <c>:</c> or empty name). Array/object variables
    /// never match the <c>var</c> filter.
    /// </remarks>
    public static async Task<IResult> ListInstances(
        string? status,
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        [FromQuery(Name = "var")] string[]? variables,
        bool? includeVariables,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        _ = actorResolver.Resolve(principal);
        var (p, s) = NormalizePaging(page, pageSize);
        return Results.Ok(await service.ListInstancesAsync(status, instanceId, workflowId, workflowKey, businessKey, nodeId, nodeExternalId, variables, includeVariables ?? false, p, s, cancellationToken));
    }

    /// <summary>
    /// Retrieves user tasks pending claim or action for the current authenticated actor.
    /// </summary>
    /// <param name="instanceId">Optional. Filter by unique instance ID.</param>
    /// <param name="workflowId">Optional. Filter by workflow definition version ID.</param>
    /// <param name="workflowKey">Optional. Filter by stable workflow key (spans all versions).</param>
    /// <param name="businessKey">Optional. Exact, case-sensitive business-key match after trimming.</param>
    /// <param name="nodeId">Optional. Filter by flow node ID.</param>
    /// <param name="nodeExternalId">Optional. Filter by flow node external ID (case-insensitive).</param>
    /// <param name="variables">Optional. Repeated <c>var=name:value</c> filters; exact case-insensitive match on an instance variable's latest scalar value, AND-combined.</param>
    /// <param name="page">Optional. The 1-based page index (default 1).</param>
    /// <param name="pageSize">Optional. The number of items per page (default 50, max 200).</param>
    /// <param name="principal">The security principal of the current actor.</param>
    /// <param name="actorResolver">Resolves the configured canonical actor identity from the principal.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Actor-scoped: returns only <c>running</c> instances resting on a <c>userTask</c>
    /// whose node roles (if any) the caller holds, minus tasks already claimed by someone
    /// else. Tasks the caller has claimed are included and flagged with
    /// <c>ClaimedByMe = true</c>. Each <see cref="InboxItemDto"/> carries precomputed
    /// <c>CanClaim</c>/<c>CanAct</c> flags based on the caller's roles. Directly assigned
    /// tasks are matched to the authenticated username case-insensitively. Counting and
    /// paging are performed in SQL. A 400 is returned for a malformed <c>var</c> entry.
    /// </remarks>
    public static async Task<IResult> GetInbox(
        long? instanceId,
        long? workflowId,
        string? workflowKey,
        string? businessKey,
        int? nodeId,
        string? nodeExternalId,
        [FromQuery(Name = "var")] string[]? variables,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var (p, s) = NormalizePaging(page, pageSize);
        return Results.Ok(await service.GetInboxAsync(actorResolver.Resolve(principal), instanceId, workflowId, workflowKey, businessKey, nodeId, nodeExternalId, variables, p, s, cancellationToken));
    }

    /// <summary>
    /// Retrieves full structural and execution details of a specific workflow instance.
    /// </summary>
    /// <param name="id">The database ID of the workflow instance.</param>
    /// <param name="principal">The security principal containing the actor identity.</param>
    /// <param name="actorResolver">Validates the configured canonical actor identity.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Returns the full <see cref="InstanceDetailDto"/>: the embedded workflow definition,
    /// the current resting node, the complete list of instance variables, and the full
    /// execution history. Returns 404 when the instance does not exist.
    /// </remarks>
    public static async Task<IResult> GetInstance(
        long id,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        _ = actorResolver.Resolve(principal);
        var instance = await service.GetInstanceAsync(id, cancellationToken);
        return instance is null ? Results.NotFound() : Results.Ok(instance);
    }

    /// <summary>
    /// Lists all sequence flows currently available to be taken from the resting node of a workflow instance.
    /// </summary>
    /// <param name="id">The database ID of the workflow instance.</param>
    /// <param name="principal">The security principal of the current actor.</param>
    /// <param name="actorResolver">Resolves the configured canonical actor identity from the principal.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Returns the <see cref="SequenceFlowModel"/> list the current actor may take, after
    /// the engine applies direct assignment, node roles, sequence-flow roles, claim
    /// ownership, and (for <c>exclusiveGateway</c> / <c>userTask</c> flows) the flow
    /// <c>condition</c>/<c>isDefault</c> rules. Returns an empty array (not
    /// 404) when the instance does not exist, is not running, is not resting on a
    /// <c>userTask</c>, or the actor is not assigned/authorized.
    /// </remarks>
    public static async Task<IResult> GetAvailableFlows(
        long id,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        return Results.Ok(await service.GetAvailableFlowsAsync(id, actorResolver.Resolve(principal), cancellationToken));
    }

    private static async Task<IResult> ListUserTasks(
        long id,
        string? status,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var paging = NormalizePaging(page, pageSize);
        return Results.Ok(await service.ListUserTasksAsync(id, status, paging.Page, paging.PageSize,
            actorResolver.Resolve(principal), cancellationToken));
    }

    /// <summary>
    /// Claims a userTask within a workflow instance for the current authenticated actor.
    /// </summary>
    /// <param name="id">The database ID of the workflow instance.</param>
    /// <param name="principal">The security principal of the current actor claiming the task.</param>
    /// <param name="actorResolver">Resolves the configured canonical actor identity from the principal.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Locks the resting <c>userTask</c> to the current actor (required before taking any
    /// flow when the node has <c>requiresClaim</c> = true). The actor must hold one of the
    /// node's roles (if any are set); the task must not already be claimed by someone
    /// else. The claim is released when a flow is taken. Returns the updated
    /// <see cref="InstanceDetailDto"/>. A 400 is returned for a non-claimable node, a role
    /// mismatch, a directly assigned task, or an already-claimed task; 404 when the instance
    /// does not exist.
    /// </remarks>
    public static async Task<IResult> ClaimInstance(
        long id,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var instance = await service.ClaimAsync(id, actorResolver.Resolve(principal), cancellationToken);
        return instance is null ? Results.NotFound() : Results.Ok(instance);
    }

    /// <summary>
    /// Releases a previously claimed userTask, returning it to the pool of unclaimed tasks.
    /// </summary>
    /// <param name="id">The database ID of the workflow instance.</param>
    /// <param name="principal">The security principal of the current actor releasing the claim.</param>
    /// <param name="actorResolver">Resolves the configured canonical actor identity from the principal.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Clears the claim on the resting <c>userTask</c> so another actor can claim it. The
    /// caller must be the current claimant or hold a role listed in the workflow's
    /// <c>unclaimRoles</c>. Returns the updated <see cref="InstanceDetailDto"/>. A 400 is
    /// returned when the caller is neither the claimant nor in <c>unclaimRoles</c>; 404 when
    /// the instance does not exist.
    /// </remarks>
    public static async Task<IResult> UnclaimInstance(
        long id,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var instance = await service.UnclaimAsync(id, actorResolver.Resolve(principal), cancellationToken);
        return instance is null ? Results.NotFound() : Results.Ok(instance);
    }

    /// <summary>
    /// Executes a transition flow from the current resting node to the next step.
    /// </summary>
    /// <param name="id">The database ID of the workflow instance.</param>
    /// <param name="flowId">The unique integer ID of the sequence flow to transition along.</param>
    /// <param name="request">Transition payload containing values for variables to submit/set.</param>
    /// <param name="principal">The security principal of the current actor.</param>
    /// <param name="actorResolver">Resolves the configured canonical actor identity from the principal.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Takes the named sequence flow from the resting <c>userTask</c>: validates the
    /// caller's roles (node + flow), claim ownership (if <c>requiresClaim</c>), the flow's
    /// own <c>condition</c>/<c>isDefault</c> (if any), and the declared flow variables
    /// (required presence + NCalc <c>validation</c>). Then runs pass-through routing in the
    /// same transaction - automatic nodes (task/serviceTask/scriptTask/exclusiveGateway,
    /// including errorBoundaryEvent error paths) are resolved until the instance rests on
    /// the next <c>userTask</c> or <c>intermediateMessageCatchEvent</c>, or terminates on an
    /// <c>endEvent</c>/<c>errorEndEvent</c>. Returns the updated <see cref="InstanceDetailDto"/>.
    ///
    /// A 400 is returned for an unavailable flow, a role/claim mismatch, a missing required
    /// variable, a failed validation rule, a gateway with no matching/default flow, or a
    /// service/script task failure with no attached error boundary; 404 when the instance
    /// does not exist; 409 when the instance is no longer running.
    /// </remarks>
    public static async Task<IResult> TakeFlow(
        long id,
        int flowId,
        TakeFlowRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        var instance = await service.TakeFlowAsync(
            id,
            flowId,
            actorResolver.Resolve(principal),
            request.Variables,
            cancellationToken);
        return instance is null ? Results.NotFound() : Results.Ok(instance);
    }

    /// <summary>
    /// Cancels an active workflow instance.
    /// </summary>
    /// <param name="id">The database ID of the workflow instance to cancel.</param>
    /// <param name="principal">The security principal of the current actor.</param>
    /// <param name="actorResolver">Resolves the configured canonical actor identity from the principal.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// Transitions a <c>running</c> instance to the <c>cancelled</c> status. The caller must
    /// hold a role listed in the workflow's <c>cancelRoles</c>. Returns 204 on success. A
    /// 400 is returned when the caller lacks a cancel role; 404 when the instance does not
    /// exist; 409 when it is no longer running.
    /// </remarks>
    public static async Task<IResult> CancelInstance(
        long id,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IWorkflowEngineService service,
        CancellationToken cancellationToken)
    {
        return await service.CancelAsync(id, actorResolver.Resolve(principal), cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    /// <summary>
    /// Delivers an external system message payload to a workflow instance resting on an intermediate message catch event.
    /// </summary>
    /// <param name="context">The HTTP request context containing correlation and credential headers.</param>
    /// <param name="id">The database ID of the workflow instance waiting for the message.</param>
    /// <param name="service">The workflow engine service.</param>
    /// <param name="options">Deployment-wide inbound message payload limits.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <remarks>
    /// This endpoint is <c>AllowAnonymous</c> - authentication is the catch node's configured
    /// client id/secret (sent as <c>X-Client-Id</c>/<c>X-Client-Secret</c>) plus a required
    /// custom header named by the node's <c>headerName</c> whose value must equal
    /// <c>headerValue</c> (and satisfy the optional NCalc <c>headerValidation</c> rule, with
    /// the incoming header value bound as <c>header</c>). The raw JSON message body is then
    /// mapped through typed <c>outputMappings</c>. External JSON types are strict; missing
    /// paths may use ordered defaults; required final values and mapping/process NCalc rules
    /// are checked against the final overlay before any variable is written. A failure rejects
    /// the delivery with 400 and leaves the instance waiting; success advances down the catch
    /// node's single outgoing flow.
    ///
    /// Returns a slim <see cref="MessageDeliveryAckDto"/> (no definition/variables/history,
    /// since the endpoint is anonymous). Correlation is by instance id only - the instance
    /// must be <c>running</c> and currently resting on the catch node. A 401 is returned on a
    /// client id/secret mismatch; a 400 for a missing/mismatched header, a failed
    /// <c>headerValidation</c> rule, a required-mapping failure, or a not-running/not-waiting
    /// instance; 404 when the instance does not exist. A catch configured with
    /// <c>message.deliveryIdempotency=true</c> requires the configured
    /// <c>message.deliveryIdempotencyHeaderName</c> (default <c>Idempotency-Key</c>)
    /// and returns 409 for every authenticated reuse of a committed instance-scoped key.
    /// </remarks>
    public static async Task<IResult> DeliverMessage(
        HttpContext context,
        long id,
        IWorkflowEngineService service,
        MessageDeliveryOptions options,
        CancellationToken cancellationToken)
    {
        var clientId = context.Request.Headers["X-Client-Id"].FirstOrDefault();
        var clientSecret = context.Request.Headers["X-Client-Secret"].FirstOrDefault();
        var headers = ToHeaderDictionary(context.Request.Headers);

        Log.Information("Message delivery request to instance {InstanceId} from client '{ClientId}'", id, clientId);

        var payloadRead = await ReadMessagePayloadAsync(
            context.Request,
            options.MaxPayloadBytes,
            cancellationToken);
        if (payloadRead.Error is not null)
        {
            return payloadRead.Error;
        }

        var actor = new ActorContext(clientId, [], new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var message = new IncomingMessage(clientId, clientSecret, headers, payloadRead.Payload, actor);
        var ack = await service.DeliverMessageAsync(id, message, cancellationToken);
        if (ack is null)
        {
            Log.Information("Message delivery to instance {InstanceId}: instance not found.", id);
            return Results.NotFound();
        }

        Log.Information("Message delivery to instance {InstanceId} acknowledged. Status: {Status}, resting on node {NodeId}.",
            id, ack.Status, ack.CurrentNodeId);
        return Results.Ok(ack);
    }

    private static async Task<MessagePayloadReadResult> ReadMessagePayloadAsync(
        HttpRequest request,
        int maxPayloadBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maxPayloadBytes)
        {
            return MessagePayloadReadResult.Failed(Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Message payload is too large."));
        }

        await using var body = new MemoryStream(
            request.ContentLength is > 0
                ? (int)Math.Min(request.ContentLength.Value, maxPayloadBytes)
                : 0);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > maxPayloadBytes)
            {
                return MessagePayloadReadResult.Failed(Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Message payload is too large."));
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (body.Length == 0)
        {
            return MessagePayloadReadResult.Succeeded(null);
        }

        if (!request.HasJsonContentType())
        {
            return MessagePayloadReadResult.Failed(Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "A non-empty message payload must use a JSON media type."));
        }

        try
        {
            using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
            return MessagePayloadReadResult.Succeeded(document.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse an incoming message JSON payload.");
            return MessagePayloadReadResult.Failed(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The message payload is not valid JSON."));
        }
    }

    private sealed record MessagePayloadReadResult(JsonElement? Payload, IResult? Error)
    {
        public static MessagePayloadReadResult Succeeded(JsonElement? payload) => new(payload, null);

        public static MessagePayloadReadResult Failed(IResult error) => new(null, error);
    }

    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ToHeaderDictionary(
        IHeaderDictionary headers) =>
        headers.ToDictionary(
            header => header.Key,
            header => (IReadOnlyList<string>)header.Value.Select(value => value ?? string.Empty).ToArray(),
            StringComparer.OrdinalIgnoreCase);

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

}
