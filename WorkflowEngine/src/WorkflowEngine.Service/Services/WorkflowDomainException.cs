namespace WorkflowEngine.Service.Services;

public sealed class WorkflowDomainException(string message) : InvalidOperationException(message);
