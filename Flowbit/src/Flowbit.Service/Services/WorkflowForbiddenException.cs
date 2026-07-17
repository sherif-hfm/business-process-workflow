namespace Flowbit.Service.Services;

public sealed class WorkflowForbiddenException(string message) : Exception(message);
