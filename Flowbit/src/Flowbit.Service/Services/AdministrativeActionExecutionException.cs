namespace Flowbit.Service.Services;

/// <summary>
/// Identifies a failure that occurred after an administrative action passed its
/// frozen-position and submitted-variable checks and began downstream routing.
/// The surrounding workflow transaction is rolled back, while the durable
/// batch worker records the item as failed rather than as a stale/skipped item.
/// </summary>
internal sealed class AdministrativeActionExecutionException(
    string message,
    Exception innerException)
    : Exception(message, innerException);
