using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using WorkflowEngine.Service.Abstractions;

namespace WorkflowEngine.Infrastructure.Http;

/// <summary>
/// Executes service-task REST calls over <see cref="HttpClient"/>. Transport
/// failures (timeout / network) are reported as a non-completed result with
/// status 0 so the engine can route to an attached errorBoundaryEvent (or, when
/// none is attached, fail the transition); a completed call reports its real
/// status code and body regardless of success.
/// </summary>
public sealed class HttpServiceTaskInvoker(HttpClient httpClient, ILogger<HttpServiceTaskInvoker> logger) : IServiceTaskInvoker
{
    public async Task<ServiceTaskResult> InvokeAsync(
        ServiceTaskRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(request.TimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var httpRequest = new HttpRequestMessage(
            new HttpMethod(request.Method),
            request.Url);

        var hasContentType = false;
        if (request.Body is not null)
        {
            httpRequest.Content = new StringContent(request.Body, Encoding.UTF8);
            // StringContent defaults to text/plain; clear it so an explicit header wins.
            httpRequest.Content.Headers.ContentType = null;
        }

        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (httpRequest.Content is not null
                    && MediaTypeHeaderValue.TryParse(header.Value, out var mediaType))
                {
                    httpRequest.Content.Headers.ContentType = mediaType;
                    hasContentType = true;
                }

                continue;
            }

            if (!httpRequest.Headers.TryAddWithoutValidation(header.Name, header.Value)
                && httpRequest.Content is not null)
            {
                httpRequest.Content.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }
        }

        if (request.Body is not null && !hasContentType && httpRequest.Content is not null)
        {
            httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
            {
                CharSet = "utf-8"
            };
        }

        logger.LogInformation("Sending outbound REST request to {Method} {Url} with timeout {TimeoutSeconds}s", request.Method, request.Url, request.TimeoutSeconds);

        try
        {
            using var response = await httpClient.SendAsync(httpRequest, linkedCts.Token);
            var body = await response.Content.ReadAsStringAsync(linkedCts.Token);
            logger.LogInformation("Outbound REST request to {Method} {Url} completed with status {StatusCode}", request.Method, request.Url, (int)response.StatusCode);
            return new ServiceTaskResult(true, (int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            logger.LogWarning("Outbound REST request to {Method} {Url} timed out after {TimeoutSeconds}s", request.Method, request.Url, request.TimeoutSeconds);
            return new ServiceTaskResult(false, 0, null, $"Request timed out after {request.TimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Outbound REST request to {Method} {Url} failed", request.Method, request.Url);
            return new ServiceTaskResult(false, 0, null, ex.Message);
        }
    }
}
