using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Flowbit.Service.Abstractions;

namespace Flowbit.Infrastructure.Http;

/// <summary>
/// Executes service-task REST calls over <see cref="HttpClient"/>. Transport
/// failures (timeout / network) are reported as a non-completed result with
/// status 0 so the engine can route to an attached errorBoundaryEvent (or, when
/// none is attached, fail the transition); a completed call reports its real
/// status code and body regardless of success.
/// </summary>
public sealed class HttpServiceTaskInvoker(
    HttpClient httpClient,
    ServiceTaskOptions options,
    ILogger<HttpServiceTaskInvoker> logger) : IServiceTaskInvoker
{
    public async Task<ServiceTaskResult> InvokeAsync(
        ServiceTaskRequest request,
        CancellationToken cancellationToken)
    {
        var statusCode = 0;
        var origin = "unresolved destination";
        try
        {
            if (request.TimeoutSeconds <= 0 || request.TimeoutSeconds > options.MaxTimeoutSeconds)
            {
                return InvalidRequest(
                    $"Timeout must be between 1 and {options.MaxTimeoutSeconds} seconds.");
            }

            if (options.MaxResponseBodyBytes <= 0)
            {
                return InvalidRequest("The configured response body limit is invalid.");
            }

            if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Fragment))
            {
                return InvalidRequest(
                    "The resolved URL must be absolute HTTP(S) without embedded credentials or a fragment.");
            }

            origin = uri.GetLeftPart(UriPartial.Authority);

            using var timeoutCts = new CancellationTokenSource(
                TimeSpan.FromSeconds(request.TimeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutCts.Token);
            using var httpRequest = new HttpRequestMessage(
                new HttpMethod(request.Method),
                uri);

            var hasContentType = false;
            if (request.Body is not null)
            {
                httpRequest.Content = new StringContent(request.Body, Encoding.UTF8);
                // StringContent defaults to text/plain; clear it so an explicit header wins.
                httpRequest.Content.Headers.ContentType = null;
            }

            foreach (var header in request.Headers)
            {
                if (string.IsNullOrWhiteSpace(header.Name)
                    || header.Name.Length > 300
                    || !Regex.IsMatch(header.Name, @"^[!#$%&'*+\-.^_`|~0-9A-Za-z]+$"))
                {
                    return InvalidRequest("A request header name is invalid.");
                }

                if (IsForbiddenFramingHeader(header.Name))
                {
                    return InvalidRequest("Request-framing headers cannot be configured by a service task.");
                }

                if (header.Value is null || header.Value.Contains('\r') || header.Value.Contains('\n'))
                {
                    return InvalidRequest("A request header value is invalid.");
                }

                if (string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (httpRequest.Content is not null)
                    {
                        if (!MediaTypeHeaderValue.TryParse(header.Value, out var mediaType))
                        {
                            return InvalidRequest("The Content-Type header value is invalid.");
                        }

                        httpRequest.Content.Headers.ContentType = mediaType;
                        hasContentType = true;
                    }

                    continue;
                }

                var added = httpRequest.Headers.TryAddWithoutValidation(header.Name, header.Value)
                    || (httpRequest.Content is not null
                        && httpRequest.Content.Headers.TryAddWithoutValidation(header.Name, header.Value));
                if (!added)
                {
                    return InvalidRequest($"Header name '{header.Name}' is invalid for this request.");
                }
            }

            if (request.Body is not null && !hasContentType && httpRequest.Content is not null)
            {
                httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8"
                };
            }

            logger.LogInformation(
                "Sending outbound REST request using {Method} to origin {Origin} with timeout {TimeoutSeconds}s.",
                request.Method,
                origin,
                request.TimeoutSeconds);

            using var response = await httpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                linkedCts.Token);
            statusCode = (int)response.StatusCode;
            var body = await ReadBoundedBodyAsync(
                response.Content,
                options.MaxResponseBodyBytes,
                linkedCts.Token);
            logger.LogInformation(
                "Outbound REST request using {Method} to origin {Origin} completed with status {StatusCode}.",
                request.Method,
                origin,
                statusCode);
            return new ServiceTaskResult(true, statusCode, body, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Outbound REST request using {Method} to origin {Origin} timed out after {TimeoutSeconds}s.",
                request.Method,
                origin,
                request.TimeoutSeconds);
            return new ServiceTaskResult(false, 0, null, $"Request timed out after {request.TimeoutSeconds}s.");
        }
        catch (ResponseBodyTooLargeException)
        {
            logger.LogWarning(
                "Outbound REST response from origin {Origin} exceeded the configured {MaxBytes}-byte limit.",
                origin,
                options.MaxResponseBodyBytes);
            return new ServiceTaskResult(
                false,
                statusCode,
                null,
                $"Response body exceeded the configured {options.MaxResponseBodyBytes}-byte limit.");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                   or ArgumentException
                                   or InvalidOperationException
                                   or FormatException
                                   or NotSupportedException)
        {
            logger.LogWarning(
                "Outbound REST request using {Method} to origin {Origin} failed with {ExceptionType}.",
                request.Method,
                origin,
                ex.GetType().Name);
            return new ServiceTaskResult(false, statusCode, null, "Transport request failed.");
        }
    }

    private static ServiceTaskResult InvalidRequest(string reason) =>
        new(false, 0, null, $"Request configuration is invalid: {reason}");

    private static bool IsForbiddenFramingHeader(string name) =>
        name.Equals("Host", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || name.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maxBytes)
        {
            throw new ResponseBodyTooLargeException();
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var chunk = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new ResponseBodyTooLargeException();
            }

            buffer.Write(chunk, 0, read);
        }

        var encoding = ResolveEncoding(content.Headers.ContentType?.CharSet);
        return encoding.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static Encoding ResolveEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private sealed class ResponseBodyTooLargeException : Exception;
}
