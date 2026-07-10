using System;

namespace WorkflowEngine.Infrastructure.Entities;

public sealed class EngineSettingEntity
{
    public long Id { get; set; }

    public string? Namespace { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
