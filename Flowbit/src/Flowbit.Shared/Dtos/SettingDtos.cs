using System.Text.Json;

namespace Flowbit.Shared.Dtos;

public sealed record EngineSettingDto(
    long Id,
    string? Namespace,
    string Key,
    string Value,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateEngineSettingRequest(
    string? Namespace,
    string Key,
    string Value,
    string? Description = null);

public sealed record UpdateEngineSettingRequest(
    string Value,
    string? Description,
    DateTimeOffset ExpectedUpdatedAt);

public sealed record WorkflowSettingDto(
    long Id,
    string? Namespace,
    string Name,
    JsonElement Value,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record CreateWorkflowSettingRequest(
    string? Namespace,
    string Name,
    JsonElement Value,
    string? Description = null);

public sealed record UpdateWorkflowSettingRequest(
    JsonElement Value,
    string? Description,
    DateTimeOffset ExpectedUpdatedAt);
