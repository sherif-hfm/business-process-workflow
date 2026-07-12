namespace WorkflowEngine.Service.Services;

public sealed class WorkflowConflictException(string message) : Exception(message);
