using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Serilog;

namespace Flowbit.Api.Endpoints;

internal static class MessagePayloadReader
{
    public static async Task<MessagePayloadReadResult> ReadAsync(
        HttpRequest request,
        int maxPayloadBytes,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maxPayloadBytes)
        {
            return MessagePayloadReadResult.Failed(Results.Problem(
                statusCode: StatusCodes.Status413PayloadTooLarge,
                title: "Message payload is too large."));
        }

        await using var body = new MemoryStream(
            request.ContentLength is > 0
                ? (int)Math.Min(request.ContentLength.Value, maxPayloadBytes)
                : 0);
        var buffer = new byte[81920];
        while (true)
        {
            var read = await request.Body.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (body.Length + read > maxPayloadBytes)
            {
                return MessagePayloadReadResult.Failed(Results.Problem(
                    statusCode: StatusCodes.Status413PayloadTooLarge,
                    title: "Message payload is too large."));
            }

            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        if (body.Length == 0)
        {
            return MessagePayloadReadResult.Succeeded(null);
        }

        if (!request.HasJsonContentType())
        {
            return MessagePayloadReadResult.Failed(Results.Problem(
                statusCode: StatusCodes.Status415UnsupportedMediaType,
                title: "A non-empty message payload must use a JSON media type."));
        }

        try
        {
            using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
            return MessagePayloadReadResult.Succeeded(document.RootElement.Clone());
        }
        catch (JsonException ex)
        {
            Log.Warning(ex, "Failed to parse an incoming message JSON payload.");
            return MessagePayloadReadResult.Failed(Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "The message payload is not valid JSON."));
        }
    }
}

internal sealed record MessagePayloadReadResult(JsonElement? Payload, IResult? Error)
{
    public static MessagePayloadReadResult Succeeded(JsonElement? payload) => new(payload, null);

    public static MessagePayloadReadResult Failed(IResult error) => new(null, error);
}
