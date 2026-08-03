using System;

namespace Flowbit.Infrastructure.Entities;

public sealed class EngineSettingEntity
{
    public long Id { get; set; }

    public string? Namespace { get; set; }

    public string Key { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
