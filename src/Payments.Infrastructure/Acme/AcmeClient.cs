using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Payments.Application.Payments;
using Payments.Domain;
using Polly.Timeout;

namespace Payments.Infrastructure.Acme;

public sealed class AcmeClient(HttpClient httpClient, AcmeOptions options, TimeProvider timeProvider, ILogger<AcmeClient> logger) : IAcmeClient
{
    private const string Redacted = "***";

    public async Task<AcmePostResult> SubmitPaymentAsync(AcmePaymentRequest request, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var redactedJson = BuildBody(request, startedAt, redact: true);
        var stopwatch = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(HttpMethod.Post, "api/payment")
        {
            Content = new StringContent(BuildBody(request, startedAt, redact: false), System.Text.Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new("Bearer", options.ApiKey);

        try
        {
            using var response = await httpClient.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(response, body, startedAt, (int)stopwatch.ElapsedMilliseconds, redactedJson);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // No answer: the request may or may not have reached the acquirer, so the caller treats the outcome as unknown.
            logger.LogWarning("No response from Acme for TransactionId {TransactionId}: {ErrorType}", request.TransactionId, ex.GetType().Name);
            return new AcmePostResult(AcmePostKind.NoResponse, startedAt, (int)stopwatch.ElapsedMilliseconds, redactedJson, Error: ex.GetType().Name);
        }
    }

    public async Task<AcmePostResult> QueryTransactionAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var requestJson = JsonSerializer.Serialize(new { Query = "TransactionID", TransactionID = transactionId });
        var stopwatch = Stopwatch.StartNew();

        using var message = new HttpRequestMessage(HttpMethod.Get, $"apirttq/query/Merchant/{Uri.EscapeDataString(options.MerchantId)}?TransactionID={transactionId}");
        message.Headers.Authorization = new("Bearer", options.ApiKey);

        try
        {
            using var response = await httpClient.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse(response, body, startedAt, (int)stopwatch.ElapsedMilliseconds, requestJson);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("No response from Acme RTTQ for TransactionId {TransactionId}: {ErrorType}", transactionId, ex.GetType().Name);
            return new AcmePostResult(AcmePostKind.NoResponse, startedAt, (int)stopwatch.ElapsedMilliseconds, requestJson, Error: ex.GetType().Name);
        }
    }

    private string BuildBody(AcmePaymentRequest request, DateTimeOffset now, bool redact) =>
        JsonSerializer.Serialize(new
        {
            MessageHeader = new
            {
                MessageDateTime = Sast.FormatMessageDateTime(now),
                MessageID = request.MessageId,
                Channel = "App",
            },
            PaymentRequest = new
            {
                MerchantID = options.MerchantId,
                TransactionID = request.TransactionId,
                request.OrderNumber,
                Amount = request.AmountMinor,
                CurrencyCode = Money.Currency,
                PaymentService = "CardNotPresent",
                Card = new { Token = redact ? Redacted : request.CardToken },
                CardOnFileScenario = "CIT",
                SubMerchantID = request.SubMerchantId,
                MerchantCategoryCode = request.Mcc,
                request.CardAcceptorName,
                request.Email,
            },
        });

    // Tolerant reader: only the fields we use are read; unknown fields are ignored.
    private static AcmePostResult Parse(HttpResponseMessage response, string body, DateTimeOffset startedAt, int durationMs, string redactedJson)
    {
        var status = (int)response.StatusCode;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var detail = root.TryGetProperty("PaymentResponse", out var payment) ? payment : root;
            var responseDetail = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("ResponseDetail", out var rd) ? rd : default;

            return new AcmePostResult(
                AcmePostKind.Responded,
                startedAt,
                durationMs,
                redactedJson,
                status,
                body,
                ReadString(root, "MessageHeader", "MessageID"),
                ReadString(detail, "TransactionStatus"),
                ReadString(responseDetail, "Source"),
                ReadString(responseDetail, "Code"),
                detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("AuthAmount", out var amount) && amount.ValueKind == JsonValueKind.Number && amount.TryGetInt64(out var value) ? value : null,
                ResponseTransactionId: ReadString(detail, "TransactionID"),
                RetryAfter: ReadRetryAfter(response),
                TransactionAt: Sast.TryParseUnzoned(ReadString(detail, "TransactionDateTime")));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Acme answered, so the interaction must still be recorded even when the body is not what we expect.
            return new AcmePostResult(AcmePostKind.Responded, startedAt, durationMs, redactedJson, status, Error: "UnparseableResponse", RetryAfter: ReadRetryAfter(response));
        }
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter is { } header ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null) : null;

    private static string? ReadString(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element))
            {
                return null;
            }
        }

        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }
}
