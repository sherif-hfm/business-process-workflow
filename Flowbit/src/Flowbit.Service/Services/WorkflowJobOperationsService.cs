using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Services;

public sealed class WorkflowJobOperationsService(
    IWorkflowJobRepository repository,
    IEngineSettingsRepository engineSettings,
    TimeProvider timeProvider) : IWorkflowJobOperationsService
{
    public const string RequiredRoleSettingKey = "WorkflowJobs.RequiredRole";
    public const string DefaultRequiredRole = "admin";

    public async Task<JobQueueStatisticsDto> GetQueueStatisticsAsync(
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(actor, cancellationToken);
        var statistics = await repository.GetQueueStatisticsAsync(cancellationToken);
        var queueLagSeconds = statistics.OldestRunnableDueAt is { } oldestDueAt
            ? Math.Max(0d, (statistics.ObservedAt - oldestDueAt).TotalSeconds)
            : 0d;
        return new JobQueueStatisticsDto(
            statistics.RunnableDepth,
            statistics.OldestRunnableDueAt,
            queueLagSeconds,
            statistics.TimerControlRunnableCount,
            statistics.ActiveLeaseCount,
            statistics.OpenIncidentCount,
            statistics.ObservedAt);
    }

    public async Task<PagedResult<JobSummaryDto>> SearchJobsAsync(
        WorkflowJobQuery query,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(actor, cancellationToken);
        ValidateCursor(query.Cursor, WorkflowJobCursor.TryDecodeJob, "job");
        ValidateKeysetPage(query.Cursor, query.Page, "job");
        var normalized = query with
        {
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize, 1, 200)
        };
        var page = await repository.SearchJobsAsync(normalized, cancellationToken);
        return new PagedResult<JobSummaryDto>(
            page.Items.Select(ToSummary).ToArray(),
            page.Page,
            page.PageSize,
            page.TotalCount)
        {
            NextCursor = page.NextCursor
        };
    }

    public async Task<JobDetailDto?> GetJobAsync(
        long jobId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ValidateId(jobId, "job");
        await AuthorizeAsync(actor, cancellationToken);
        var job = await repository.GetAsync(jobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        return new JobDetailDto(
            ToSummary(job),
            job.ActivationId,
            job.WorkerId,
            job.LeaseGeneration,
            job.StartedAt,
            job.ResultReadyAt,
            job.LastFailureCode ?? ReadError(job.Error, "code"),
            job.LastFailureDescription
                ?? ReadError(job.Error, "description")
                ?? ReadError(job.Error, "message"));
    }

    public async Task<PagedResult<JobAttemptDto>> ListAttemptsAsync(
        long jobId,
        string? cursor,
        int pageSize,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ValidateId(jobId, "job");
        await AuthorizeAsync(actor, cancellationToken);
        if (!string.IsNullOrWhiteSpace(cursor)
            && !WorkflowJobCursor.TryDecodeAttempt(cursor, out _, out _))
        {
            throw new WorkflowDomainException("The attempt cursor is invalid or expired.");
        }
        var result = await repository.ListAttemptsAsync(
            jobId,
            cursor,
            Math.Clamp(pageSize, 1, 200),
            cancellationToken);
        return new PagedResult<JobAttemptDto>(
            result.Items.Select(static attempt => new JobAttemptDto(
                attempt.Id,
                attempt.JobId,
                attempt.AttemptNumber,
                attempt.Status,
                attempt.WorkerId,
                attempt.LeaseGeneration,
                attempt.StartedAt,
                attempt.FinishedAt,
                attempt.FailureCode,
                attempt.FailureDescription)).ToArray(),
            result.Page,
            result.PageSize,
            result.TotalCount)
        {
            NextCursor = result.NextCursor
        };
    }

    public async Task<PagedResult<IncidentSummaryDto>> SearchIncidentsAsync(
        WorkflowIncidentQuery query,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        await AuthorizeAsync(actor, cancellationToken);
        ValidateCursor(query.Cursor, WorkflowJobCursor.TryDecodeIncident, "incident");
        ValidateKeysetPage(query.Cursor, query.Page, "incident");
        var normalized = query with
        {
            Page = Math.Max(1, query.Page),
            PageSize = Math.Clamp(query.PageSize, 1, 200)
        };
        var page = await repository.SearchIncidentsAsync(normalized, cancellationToken);
        return new PagedResult<IncidentSummaryDto>(
            page.Items.Select(ToSummary).ToArray(),
            page.Page,
            page.PageSize,
            page.TotalCount)
        {
            NextCursor = page.NextCursor
        };
    }

    public async Task<IncidentDetailDto?> GetIncidentAsync(
        long incidentId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ValidateId(incidentId, "incident");
        await AuthorizeAsync(actor, cancellationToken);
        var incident = await repository.GetIncidentAsync(incidentId, cancellationToken);
        if (incident is null)
        {
            return null;
        }

        var job = await repository.GetAsync(incident.JobId, cancellationToken);
        return new IncidentDetailDto(
            ToSummary(incident),
            job is null ? null : ToSummary(job),
            incident.Details,
            incident.ResolvedBy);
    }

    public async Task<RetryIncidentResultDto?> RetryIncidentAsync(
        long incidentId,
        ActorContext actor,
        CancellationToken cancellationToken)
    {
        ValidateId(incidentId, "incident");
        await AuthorizeAsync(actor, cancellationToken);
        var dueAt = timeProvider.GetUtcNow();
        var job = await repository.RetryIncidentAsync(
            incidentId,
            actor.User ?? "anonymous",
            dueAt,
            cancellationToken);
        return job is null
            ? null
            : new RetryIncidentResultDto(
                incidentId,
                job.Id,
                WorkflowIncidentStatuses.Resolved,
                job.Status,
                job.DueAt);
    }

    private async Task AuthorizeAsync(ActorContext actor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var setting = await engineSettings.GetByKeyAsync(RequiredRoleSettingKey, cancellationToken);
        var required = ParseRoles(setting?.Value);
        var authorized = actor.Roles
            .Where(static caller => !string.IsNullOrWhiteSpace(caller))
            .Any(caller =>
                required.Contains(caller.Trim(), StringComparer.OrdinalIgnoreCase));
        if (!authorized)
        {
            throw new WorkflowForbiddenException(
                $"A {RequiredRoleSettingKey} role is required to inspect or retry workflow jobs.");
        }
    }

    internal static IReadOnlyList<string> ParseRoles(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [DefaultRequiredRole];
        }

        var roles = value.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static role => role.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return roles.Length == 0 ? [DefaultRequiredRole] : roles;
    }

    private static JobSummaryDto ToSummary(WorkflowJobRecord job) =>
        new(
            job.Id,
            job.InstanceId,
            job.WorkflowDefinitionId,
            job.WorkflowKey,
            job.TokenId,
            job.NodeId,
            job.NodeName,
            job.NodeType,
            job.Kind,
            job.Phase,
            job.QueueClass,
            job.Status,
            job.AttemptCount,
            job.DueAt,
            job.LeaseExpiresAt,
            job.IncidentId,
            job.CreatedAt,
            job.UpdatedAt,
            job.CompletedAt);

    private static IncidentSummaryDto ToSummary(WorkflowIncidentRecord incident) =>
        new(
            incident.Id,
            incident.JobId,
            incident.InstanceId,
            incident.WorkflowDefinitionId,
            incident.WorkflowKey,
            incident.NodeId,
            incident.NodeName,
            incident.Type,
            incident.Status,
            incident.Summary,
            incident.CreatedAt,
            incident.UpdatedAt,
            incident.ResolvedAt);

    private static string? ReadError(JsonElement? error, string propertyName)
    {
        if (error is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return property.GetString();
    }

    private static void ValidateId(long value, string name)
    {
        if (value <= 0)
        {
            throw new WorkflowDomainException($"{name} id must be greater than zero.");
        }
    }

    private delegate bool UpdatedCursorDecoder(
        string? cursor,
        out DateTimeOffset updatedAt,
        out long id);

    private static void ValidateCursor(
        string? cursor,
        UpdatedCursorDecoder decoder,
        string name)
    {
        if (!string.IsNullOrWhiteSpace(cursor)
            && !decoder(cursor, out _, out _))
        {
            throw new WorkflowDomainException(
                $"The {name} cursor is invalid or expired.");
        }
    }

    private static void ValidateKeysetPage(
        string? cursor,
        int page,
        string name)
    {
        if (string.IsNullOrWhiteSpace(cursor) && page > 1)
        {
            throw new WorkflowDomainException(
                $"{name} pages after the first require the opaque cursor returned by the preceding page.");
        }
    }
}
