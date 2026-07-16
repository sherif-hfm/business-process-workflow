using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Endpoints;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth")
            .WithTags("Authentication")
            .RequireAuthorization();

        group.MapGet("/context", GetContext)
            .Produces<ActorContextDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized);

        return app;
    }

    private static IResult GetContext(
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver)
    {
        var actor = actorResolver.Resolve(principal);
        return Results.Ok(new ActorContextDto(actor.User, actor.Roles.ToArray()));
    }
}
