using System.Net.Http.Headers;

namespace WorkflowEngine.Ui.Auth;

/// <summary>
/// Attaches the current <see cref="TokenState"/> JWT as a Bearer header to every
/// outgoing API request.
/// </summary>
public sealed class AuthTokenHandler(TokenState tokenState) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = tokenState.Token;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
