using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc;

namespace Flowbit.Api.Endpoints;

public static class InstanceVariableUpdateEndpoints
{
    private const long MaxRequestBodyBytes = 1024 * 1024;

    public static IEndpointRouteBuilder MapInstanceVariableUpdateEndpoints(
        this IEndpointRouteBuilder app)
    {
        app.MapPatch("/api/instances/{id:long}/variables", UpdateVariables)
            .WithTags("Workflow Instances")
            .RequireAuthorization()
            .RequireWorkflowAdministrator()
            .Accepts<UpdateInstanceVariablesRequest>("application/json")
            .WithMetadata(new RequestSizeLimitAttribute(MaxRequestBodyBytes))
            .WithSummary("Administratively add or update variables on one running instance")
            .WithDescription(
                "Appends raw JSON variable-history values atomically without applying the workflow definition's " +
                "declared type, nullability, or NCalc validation contracts. JSON null is a stored value, not deletion.")
            .Produces<UpdateInstanceVariablesResultDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status413PayloadTooLarge)
            .Produces(StatusCodes.Status415UnsupportedMediaType);

        return app;
    }

    private static async Task<IResult> UpdateVariables(
        long id,
        UpdateInstanceVariablesRequest request,
        ClaimsPrincipal principal,
        IActorContextResolver actorResolver,
        IInstanceVariableUpdateService service,
        CancellationToken cancellationToken)
    {
        var result = await service.UpdateAsync(
            id,
            request,
            actorResolver.Resolve(principal),
            cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
}
