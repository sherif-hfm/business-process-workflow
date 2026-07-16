namespace Flowbit.Service.Services;

public class WorkflowConflictException(string message) : Exception(message);

public sealed class BusinessKeyConflictException(long existingInstanceId)
    : WorkflowConflictException("A workflow instance already owns this business key.")
{
    public long ExistingInstanceId { get; } = existingInstanceId;
}

public sealed class IdempotencyKeyConflictException(long existingInstanceId)
    : WorkflowConflictException("A workflow instance already owns this idempotency key.")
{
    public long ExistingInstanceId { get; } = existingInstanceId;
}
