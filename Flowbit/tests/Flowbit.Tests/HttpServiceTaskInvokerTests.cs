using System.Diagnostics;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Flowbit.Infrastructure.Http;
using Flowbit.Service.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class HttpServiceTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_SendsResolvedRequestAndReturnsCompletedResponse()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        using var client = Client(async (request, _) =>
        {
            captured = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync();
            return Response(HttpStatusCode.Accepted, "{\"ok\":true}");
        });
        var invoker = CreateInvoker(client);

        var result = await invoker.InvokeAsync(
            Request(
                body: "{\"name\":\"alice\"}",
                headers:
                [
                    new ServiceTaskHeader("X-Correlation", "abc"),
                    new ServiceTaskHeader("Content-Type", "application/problem+json")
                ]),
            CancellationToken.None);

        Assert.True(result.Completed);
        Assert.True(result.IsSuccess);
        Assert.Equal(202, result.StatusCode);
        Assert.Equal("{\"ok\":true}", result.Body);
        Assert.Null(result.Error);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Equal("abc", captured.Headers.GetValues("X-Correlation").Single());
        Assert.Equal("application/problem+json", captured.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("{\"name\":\"alice\"}", capturedBody);
    }

    [Fact]
    public async Task InvokeAsync_DefaultsBodyContentTypeToJson()
    {
        string? mediaType = null;
        using var client = Client((request, _) =>
        {
            mediaType = request.Content?.Headers.ContentType?.MediaType;
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        });

        var result = await CreateInvoker(client).InvokeAsync(Request(body: "{}"), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("application/json", mediaType);
    }

    [Fact]
    public async Task InvokeAsync_RejectsResponseAboveConfiguredLimitAndKeepsStatus()
    {
        using var client = Client((_, _) =>
            Task.FromResult(Response(HttpStatusCode.OK, "123456")));
        var invoker = CreateInvoker(client, maxResponseBodyBytes: 5);

        var result = await invoker.InvokeAsync(Request(), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.False(result.IsSuccess);
        Assert.Equal(200, result.StatusCode);
        Assert.Null(result.Body);
        Assert.Contains("5-byte limit", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_UsesNodeTimeoutAndDoesNotLeakDestinationInError()
    {
        using var client = Client(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response(HttpStatusCode.OK, "{}");
        });
        var invoker = CreateInvoker(client);
        var request = Request(url: "https://secret.example.test/path?token=do-not-leak", timeoutSeconds: 1);
        var stopwatch = Stopwatch.StartNew();

        var result = await invoker.InvokeAsync(request, CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(0, result.StatusCode);
        Assert.Contains("timed out after 1s", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", result.Error, StringComparison.Ordinal);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task InvokeAsync_PropagatesCallerCancellation()
    {
        using var client = Client(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Response(HttpStatusCode.OK, "{}");
        });
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateInvoker(client).InvokeAsync(Request(timeoutSeconds: 5), cancellation.Token));
    }

    [Fact]
    public async Task InvokeAsync_ReturnsSanitizedTransportFailure()
    {
        using var client = Client((_, _) =>
            throw new HttpRequestException("DNS failed for secret.example.test?token=do-not-leak"));

        var result = await CreateInvoker(client).InvokeAsync(
            Request(url: "https://secret.example.test/path?token=do-not-leak"),
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(0, result.StatusCode);
        Assert.Equal("Transport request failed.", result.Error);
        Assert.DoesNotContain("secret.example", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-leak", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("ftp://example.test/resource")]
    [InlineData("https://user:password@example.test/resource")]
    [InlineData("https://example.test/resource#fragment")]
    public async Task InvokeAsync_RejectsUnsafeResolvedUrlsBeforeSending(string url)
    {
        var sent = false;
        using var client = Client((_, _) =>
        {
            sent = true;
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        });

        var result = await CreateInvoker(client).InvokeAsync(Request(url: url), CancellationToken.None);

        Assert.False(result.Completed);
        Assert.Equal(0, result.StatusCode);
        Assert.False(sent);
        Assert.Contains("absolute HTTP(S)", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_RejectsTimeoutAboveDeploymentMaximumBeforeSending()
    {
        var sent = false;
        using var client = Client((_, _) =>
        {
            sent = true;
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        });

        var result = await CreateInvoker(client, maxTimeoutSeconds: 10).InvokeAsync(
            Request(timeoutSeconds: 11),
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.False(sent);
        Assert.Contains("between 1 and 10", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Host", "alternate.example.test")]
    [InlineData("Bad Header", "value")]
    [InlineData("X-Test", "value\r\ninjected: true")]
    public async Task InvokeAsync_RejectsUnsafeLegacyHeadersBeforeSending(string name, string value)
    {
        var sent = false;
        using var client = Client((_, _) =>
        {
            sent = true;
            return Task.FromResult(Response(HttpStatusCode.OK, "{}"));
        });

        var result = await CreateInvoker(client).InvokeAsync(
            Request(headers: [new ServiceTaskHeader(name, value)]),
            CancellationToken.None);

        Assert.False(result.Completed);
        Assert.False(sent);
        Assert.Contains("configuration is invalid", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(value, result.Error, StringComparison.Ordinal);
    }

    private static HttpServiceTaskInvoker CreateInvoker(
        HttpClient client,
        int maxTimeoutSeconds = 300,
        int maxResponseBodyBytes = 1024) =>
        new(
            client,
            new ServiceTaskOptions
            {
                MaxTimeoutSeconds = maxTimeoutSeconds,
                MaxResponseBodyBytes = maxResponseBodyBytes
            },
            NullLogger<HttpServiceTaskInvoker>.Instance);

    private static ServiceTaskRequest Request(
        string url = "https://api.example.test/work",
        string? body = null,
        int timeoutSeconds = 5,
        IReadOnlyList<ServiceTaskHeader>? headers = null) =>
        new("POST", url, headers ?? [], body, timeoutSeconds);

    private static HttpClient Client(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new DelegateHandler(send))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string body) =>
        new(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
