using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Payments.Application.Payments;
using Payments.Domain;

namespace Payments.Api.Endpoints;

public static class CallbackEndpoints
{
    private const int MaxBodyBytes = 64 * 1024;

    public static IEndpointRouteBuilder MapCallbackEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/acme/advice", AdviceAsync);
        routes.MapPost("/acme/webhook", WebhookAsync);
        return routes;
    }

    private static async Task<IResult> AdviceAsync(HttpRequest request, CallbackSettings settings, CallbackService callbacks, CancellationToken cancellationToken)
    {
        if (!HasValidBasicCredentials(request, settings))
        {
            request.HttpContext.Response.Headers.WWWAuthenticate = "Basic realm=\"acme-advice\"";
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials");
        }

        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Body too large");
        }

        using var document = TryParse(body);
        var root = document?.RootElement;
        if (root is not { ValueKind: JsonValueKind.Object } json
            || !Guid.TryParse(GetString(json, "TransactionID"), out var transactionId)
            || GetString(json, "MerchantID") is not { Length: > 0 } merchantId
            || GetString(json, "RequestType") is not { Length: > 0 } requestType
            || GetString(json, "TransactionStatus") is not { Length: > 0 } status
            || GetString(json, "ResponseCode") is not { Length: > 0 } code)
        {
            return await RejectAsync(callbacks, EventSource.Advice, "missing-fields", body, StatusCodes.Status400BadRequest, "Notification is missing required fields", cancellationToken);
        }

        if (!string.Equals(merchantId, settings.MerchantId, StringComparison.OrdinalIgnoreCase))
        {
            return await RejectAsync(callbacks, EventSource.Advice, "merchant-mismatch", body, StatusCodes.Status403Forbidden, "Notification is for another merchant", cancellationToken);
        }

        await callbacks.HandleAdviceAsync(
            new AdviceCommand(
                transactionId,
                requestType,
                status,
                code,
                GetString(json, "ResponseSource"),
                Sast.TryParseUnzoned(GetString(json, "TransactionDateTime")),
                Encoding.UTF8.GetString(body),
                Hash(body)),
            cancellationToken);
        return Results.Ok();
    }

    private static async Task<IResult> WebhookAsync(HttpRequest request, CallbackSettings settings, CallbackService callbacks, CancellationToken cancellationToken)
    {
        var body = await ReadBodyAsync(request, cancellationToken);
        if (body is null)
        {
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Body too large");
        }

        // The signature is over the exact bytes, so it is checked before anything is parsed.
        if (!HasValidSignature(request, settings, body))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid signature");
        }

        using var document = TryParse(body);
        var root = document?.RootElement;
        if (root is not { ValueKind: JsonValueKind.Object } json
            || GetString(json, "transactionUUID") is not { Length: > 0 } uuid
            || GetString(json, "transactionType") is not { Length: > 0 } type
            || GetString(json, "rrn") is not { Length: > 0 } rrn
            || GetString(json, "txResultCode") is not { Length: > 0 } resultCode
            || !json.TryGetProperty("isApproved", out var approved) || approved.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !json.TryGetProperty("transactionAmount", out var amount) || amount.ValueKind != JsonValueKind.Number
            || !amount.TryGetInt64(out var amountMinor) || amountMinor < 0)
        {
            return await RejectAsync(callbacks, EventSource.Webhook, "missing-or-invalid-fields", body, StatusCodes.Status400BadRequest, "Notification is missing or has invalid fields", cancellationToken);
        }

        await callbacks.HandleWebhookAsync(
            new WebhookCommand(
                uuid,
                type,
                rrn,
                amountMinor,
                approved.GetBoolean(),
                resultCode,
                Sast.TryParseUnzoned(GetString(json, "transactionDateTime")),
                Encoding.UTF8.GetString(body),
                Hash(body)),
            cancellationToken);
        return Results.Ok();
    }

    private static async Task<IResult> RejectAsync(
        CallbackService callbacks,
        EventSource source,
        string reason,
        byte[] body,
        int statusCode,
        string title,
        CancellationToken cancellationToken)
    {
        await callbacks.RejectAsync(source, reason, Encoding.UTF8.GetString(body), Hash(body), cancellationToken);
        return Results.Problem(statusCode: statusCode, title: title);
    }

    private static bool HasValidBasicCredentials(HttpRequest request, CallbackSettings settings)
    {
        var header = request.Headers.Authorization.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        }
        catch (FormatException)
        {
            return false;
        }

        var separator = decoded.IndexOf(':');
        if (separator < 0)
        {
            return false;
        }

        // Both comparisons always run so timing does not reveal which part was wrong.
        var userOk = FixedEquals(decoded[..separator], settings.AdviceUsername);
        var passwordOk = FixedEquals(decoded[(separator + 1)..], settings.AdvicePassword);
        return userOk & passwordOk;
    }

    private static bool HasValidSignature(HttpRequest request, CallbackSettings settings, byte[] body)
    {
        var header = request.Headers["x-signature"].ToString();
        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(header);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.WebhookSecret), body);
        return CryptographicOperations.FixedTimeEquals(provided, expected);
    }

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));

    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static JsonDocument? TryParse(byte[] body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Unknown fields are ignored; a field of an unexpected type counts as missing.
    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Hash(byte[] body) => Convert.ToHexString(SHA256.HashData(body));
}
