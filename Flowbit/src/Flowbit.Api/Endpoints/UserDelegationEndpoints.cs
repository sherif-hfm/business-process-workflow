using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Endpoints;

public static class UserDelegationEndpoints
{
    public static IEndpointRouteBuilder MapUserDelegationEndpoints(
        this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/user-delegations")
            .WithTags("User Delegation")
            .RequireAuthorization();

        group.MapGet(string.Empty, List)
            .Produces<PagedResult<UserDelegationDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized);
        group.MapPost(string.Empty, Create)
            .Produces<IReadOnlyList<UserDelegationDto>>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{id:long}/accept", Accept)
            .Produces<UserDelegationDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{id:long}/reject", Reject)
            .Produces<UserDelegationDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/{id:long}/revoke", Revoke)
            .Produces<UserDelegationDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        group.MapGet("/manage", ListManaged)
            .Produces<PagedResult<UserDelegationDto>>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapPost("/manage", CreateManaged)
            .Produces<IReadOnlyList<UserDelegationDto>>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/manage/{id:long}/revoke", RevokeManaged)
            .Produces<UserDelegationDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        var policyGroup = app.MapGroup("/api/user-delegation-policies")
            .WithTags("User Delegation")
            .RequireAuthorization();
        policyGroup.MapGet("/{workflowKey}", GetPolicy)
            .Produces<WorkflowDelegationPolicyDto>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound);
        policyGroup.MapPut("/{workflowKey}", SetPolicy)
            .Produces<WorkflowDelegationPolicyDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> List(
        string? direction,
        string? workflowKey,
        string? state,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListAsync(
            direction,
            workflowKey,
            state,
            page ?? 1,
            pageSize ?? 50,
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> Create(
        CreateUserDelegationRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken)
    {
        var created = await service.CreateAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Created("/api/user-delegations", created);
    }

    private static Task<IResult> Accept(
        long id,
        UserDelegationLifecycleRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken) =>
        LifecycleResultAsync(service.AcceptAsync(
            id,
            request,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static Task<IResult> Reject(
        long id,
        UserDelegationLifecycleRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken) =>
        LifecycleResultAsync(service.RejectAsync(
            id,
            request,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static Task<IResult> Revoke(
        long id,
        UserDelegationLifecycleRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken) =>
        LifecycleResultAsync(service.RevokeAsync(
            id,
            request,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> ListManaged(
        string? delegator,
        [FromQuery(Name = "delegate")] string? delegateUser,
        string? workflowKey,
        string? state,
        int? page,
        int? pageSize,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken)
    {
        var result = await service.ListManagedAsync(
            delegator,
            delegateUser,
            workflowKey,
            state,
            page ?? 1,
            pageSize ?? 50,
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreateManaged(
        CreateManagedUserDelegationRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken)
    {
        var created = await service.CreateManagedAsync(
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return Results.Created("/api/user-delegations/manage", created);
    }

    private static Task<IResult> RevokeManaged(
        long id,
        UserDelegationLifecycleRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken) =>
        LifecycleResultAsync(service.RevokeManagedAsync(
            id,
            request,
            actorResolver.Resolve(principal),
            cancellationToken));

    private static async Task<IResult> GetPolicy(
        string workflowKey,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken)
    {
        var policy = await service.GetPolicyAsync(
            workflowKey,
            actorResolver.Resolve(principal),
            cancellationToken);
        return policy is null ? Results.NotFound() : Results.Ok(policy);
    }

    private static async Task<IResult> SetPolicy(
        string workflowKey,
        UpdateWorkflowDelegationPolicyRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IUserDelegationService service,
        CancellationToken cancellationToken)
    {
        var policy = await service.SetPolicyAsync(
            workflowKey,
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return policy is null ? Results.NotFound() : Results.Ok(policy);
    }

    private static async Task<IResult> LifecycleResultAsync(
        Task<UserDelegationDto?> operation)
    {
        var result = await operation;
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
