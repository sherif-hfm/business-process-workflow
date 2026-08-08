using Flowbit.Service.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Flowbit.Api.Auth;

/// <summary>
/// Applies the dynamic workflow-administrator role policy shared by workflow
/// definition management and running-instance version changes.
/// </summary>
public static class WorkflowAdministratorAuthorization
{
    private const string RequiredRoleSettingKey = "Workflow.RequiredRole";
    private const string DefaultRequiredRole = "admin";

    public static RouteHandlerBuilder RequireWorkflowAdministrator(
        this RouteHandlerBuilder endpoint)
    {
        endpoint.AddEndpointFilter(AuthorizeAsync);
        return endpoint;
    }

    public static RouteGroupBuilder RequireWorkflowAdministrator(
        this RouteGroupBuilder group)
    {
        group.AddEndpointFilter(AuthorizeAsync);
        return group;
    }

    private static async ValueTask<object?> AuthorizeAsync(
        EndpointFilterInvocationContext invocationContext,
        EndpointFilterDelegate next)
    {
        var httpContext = invocationContext.HttpContext;
        if (httpContext.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return await next(invocationContext);
        }

        var actorResolver = httpContext.RequestServices
            .GetRequiredService<IActorContextResolver>();
        var actor = actorResolver.Resolve(httpContext.User);
        var settings = httpContext.RequestServices.GetRequiredService<IEngineSettingsService>();
        var setting = await settings.GetByKeyAsync(
            RequiredRoleSettingKey,
            httpContext.RequestAborted);
        var requiredRolesText = !string.IsNullOrWhiteSpace(setting?.Value)
            ? setting.Value
            : DefaultRequiredRole;
        var requiredRoles = requiredRolesText.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requiredRoles.Length == 0)
        {
            requiredRoles = [DefaultRequiredRole];
            requiredRolesText = DefaultRequiredRole;
        }

        var userRoles = actor.Roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!requiredRoles.Any(userRoles.Contains))
        {
            Log.Warning(
                "User '{User}' with roles [{Roles}] is forbidden from workflow administration. Required role(s): '{RequiredRole}'",
                actor.User ?? "anonymous",
                string.Join(", ", userRoles),
                requiredRolesText);
            return Results.Forbid();
        }

        return await next(invocationContext);
    }
}
