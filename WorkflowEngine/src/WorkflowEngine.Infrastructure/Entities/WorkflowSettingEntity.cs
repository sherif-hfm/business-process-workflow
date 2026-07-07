using System.Text.Json;

namespace WorkflowEngine.Infrastructure.Entities;

public sealed class WorkflowSettingEntity
{
    public long Id { get; set; }

    public string? Namespace { get; set; }

    public string Name { get; set; } = string.Empty;

    public JsonElement Value { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
