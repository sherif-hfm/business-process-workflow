namespace WorkflowEngine.Service.Services;

// Thrown when a delivery caller's client id/secret or required header does not
// match the catch node's expected (templated) values, or the headerValidation
// NCalc rule rejects the incoming header value. Mapped to HTTP 401 by the API
// middleware (distinct from WorkflowDomainException's 400).
public sealed class WorkflowUnauthorizedException(string message) : InvalidOperationException(message);