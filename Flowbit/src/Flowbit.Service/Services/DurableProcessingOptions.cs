namespace Flowbit.Service.Services;

/// <summary>
/// Deployment gate for publishing definitions that require the durable worker.
/// Keep disabled while applying the additive schema/API rollout, then enable
/// after at least one worker replica reports ready.
/// </summary>
public sealed class DurableProcessingOptions
{
    public const string SectionName = "WorkflowDurableProcessing";

    public bool PublicationEnabled { get; set; }
}
