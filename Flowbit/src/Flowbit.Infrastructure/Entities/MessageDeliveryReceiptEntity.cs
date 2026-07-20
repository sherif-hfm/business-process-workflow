namespace Flowbit.Infrastructure.Entities;

public sealed class MessageDeliveryReceiptEntity
{
    public long InstanceId { get; set; }

    public WorkflowInstanceEntity? Instance { get; set; }

    public string IdempotencyKey { get; set; } = string.Empty;

    public long WaitHistoryId { get; set; }

    public InstanceHistoryEntity? WaitHistory { get; set; }

    public int SourceNodeId { get; set; }

    public string CorrelationHeaderName { get; set; } = string.Empty;

    public short ProofVersion { get; set; }

    public byte[] CredentialProofSalt { get; set; } = [];

    public byte[] CredentialProofHash { get; set; } = [];

    public byte[] EnvelopeProofSalt { get; set; } = [];

    public byte[] EnvelopeProofHash { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
