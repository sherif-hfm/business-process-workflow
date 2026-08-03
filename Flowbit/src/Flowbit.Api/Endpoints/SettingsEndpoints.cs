using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var engineSettings = app.MapGroup("/api/engine-settings")
            .WithTags("Settings")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>();

        engineSettings.MapGet(string.Empty, ListEngineSettings)
            .Produces<IReadOnlyList<EngineSettingDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        engineSettings.MapPost(string.Empty, CreateEngineSetting)
            .Produces<EngineSettingDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);
        engineSettings.MapPut("/{id:long}", UpdateEngineSetting)
            .Produces<EngineSettingDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        engineSettings.MapDelete("/{id:long}", DeleteEngineSetting)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        var workflowSettings = app.MapGroup("/api/workflow-settings")
            .WithTags("Settings")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>();

        workflowSettings.MapGet(string.Empty, ListWorkflowSettings)
            .Produces<IReadOnlyList<WorkflowSettingDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        workflowSettings.MapPost(string.Empty, CreateWorkflowSetting)
            .Produces<WorkflowSettingDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);
        workflowSettings.MapPut("/{id:long}", UpdateWorkflowSetting)
            .Produces<WorkflowSettingDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        workflowSettings.MapDelete("/{id:long}", DeleteWorkflowSetting)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> ListEngineSettings(
        IEngineSettingsService service,
        CancellationToken cancellationToken)
    {
        var settings = await service.ListAsync(cancellationToken);
        return Results.Ok(settings.Select(ToDto).ToArray());
    }

    private static async Task<IResult> CreateEngineSetting(
        CreateEngineSettingRequest request,
        IEngineSettingsService service,
        CancellationToken cancellationToken)
    {
        var setting = await service.CreateAsync(
            request.Namespace,
            request.Key,
            request.Value,
            request.Description,
            cancellationToken);
        var dto = ToDto(setting);
        return Results.Created($"/api/engine-settings/{setting.Id}", dto);
    }

    private static async Task<IResult> UpdateEngineSetting(
        long id,
        UpdateEngineSettingRequest request,
        IEngineSettingsService service,
        CancellationToken cancellationToken)
    {
        var setting = await service.UpdateAsync(
            id,
            request.Value,
            request.Description,
            request.ExpectedUpdatedAt,
            cancellationToken);
        return setting is null ? Results.NotFound() : Results.Ok(ToDto(setting));
    }

    private static async Task<IResult> DeleteEngineSetting(
        long id,
        DateTimeOffset expectedUpdatedAt,
        IEngineSettingsService service,
        CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteByIdAsync(id, expectedUpdatedAt, cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListWorkflowSettings(
        IWorkflowSettingsService service,
        CancellationToken cancellationToken)
    {
        var settings = await service.ListAsync(cancellationToken);
        return Results.Ok(settings.Select(ToDto).ToArray());
    }

    private static async Task<IResult> CreateWorkflowSetting(
        CreateWorkflowSettingRequest request,
        IWorkflowSettingsService service,
        CancellationToken cancellationToken)
    {
        var setting = await service.CreateAsync(
            request.Namespace,
            request.Name,
            request.Value,
            request.Description,
            cancellationToken);
        var dto = ToDto(setting);
        return Results.Created($"/api/workflow-settings/{setting.Id}", dto);
    }

    private static async Task<IResult> UpdateWorkflowSetting(
        long id,
        UpdateWorkflowSettingRequest request,
        IWorkflowSettingsService service,
        CancellationToken cancellationToken)
    {
        var setting = await service.UpdateAsync(
            id,
            request.Value,
            request.Description,
            request.ExpectedUpdatedAt,
            cancellationToken);
        return setting is null ? Results.NotFound() : Results.Ok(ToDto(setting));
    }

    private static async Task<IResult> DeleteWorkflowSetting(
        long id,
        DateTimeOffset expectedUpdatedAt,
        IWorkflowSettingsService service,
        CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteByIdAsync(id, expectedUpdatedAt, cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static EngineSettingDto ToDto(EngineSettingRecord setting) => new(
        setting.Id,
        setting.Namespace,
        setting.Key,
        setting.Value,
        setting.Description,
        setting.CreatedAt,
        setting.UpdatedAt);

    private static WorkflowSettingDto ToDto(WorkflowSettingRecord setting) => new(
        setting.Id,
        setting.Namespace,
        setting.Name,
        setting.Value.Clone(),
        setting.Description,
        setting.CreatedAt,
        setting.UpdatedAt);
}

public sealed class SettingsAuthorizationFilter : IEndpointFilter
{
    private const string RequiredRoleSettingKey = "Settings.RequiredRole";
    private const string DefaultRequiredRole = "admin";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var settingsService = httpContext.RequestServices.GetRequiredService<IEngineSettingsService>();
        var setting = await settingsService.GetByKeyAsync(
            RequiredRoleSettingKey,
            httpContext.RequestAborted);

        var requiredRoles = (string.IsNullOrWhiteSpace(setting?.Value)
                ? DefaultRequiredRole
                : setting.Value)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requiredRoles.Length == 0)
        {
            requiredRoles = [DefaultRequiredRole];
        }

        var actorResolver = httpContext.RequestServices.GetRequiredService<IActorContextResolver>();
        var actorRoles = actorResolver.Resolve(httpContext.User).Roles
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return requiredRoles.Any(actorRoles.Contains)
            ? await next(context)
            : Results.Forbid();
    }
}
