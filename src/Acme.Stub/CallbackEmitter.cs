using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Acme.Stub;

public sealed record StubCallbackSettings(string BaseUrl, string MerchantId, string AdviceUsername, string AdvicePassword, string WebhookSecret)
{
    public static StubCallbackSettings? From(IConfiguration configuration)
    {
        var baseUrl = configuration["Stub:CallbackBaseUrl"];
        return string.IsNullOrWhiteSpace(baseUrl)
            ? null
            : new StubCallbackSettings(
                baseUrl,
                configuration["Stub:MerchantId"] ?? "MID-LOCAL",
                configuration["Stub:AdviceUsername"] ?? "local-advice-user",
                configuration["Stub:AdvicePassword"] ?? "local-advice-password",
                configuration["Stub:WebhookSecret"] ?? "local-webhook-secret");
    }

    // Never print the credentials, even if the settings object is logged.
    public override string ToString() => $"StubCallbackSettings {{ BaseUrl = {BaseUrl} }}";
}

/// <summary>
/// Emits Payment Advice and Transaction Status webhook callbacks for a stored outcome, in the order asked, so duplicates and
/// out-of-order delivery can be demonstrated. Identical items in one script share a webhook UUID (a duplicate delivery).
/// </summary>
public sealed class CallbackEmitter(HttpClient http, StubCallbackSettings settings)
{
    public async Task<IReadOnlyList<string>> SendAsync(JsonObject outcome, string script, CancellationToken cancellationToken)
    {
        // One timestamp per script, so a repeated item is a byte-identical redelivery, as Acme's resends are.
        var sentAt = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-ddTHH:mm:ss.fff");
        var results = new List<string>();
        foreach (var item in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = item.Split(':', 2);
            var kind = parts[0].ToLowerInvariant();
            var amount = parts.Length > 1 && long.TryParse(parts[1], out var parsed) ? parsed : outcome["AuthAmount"]?.GetValue<long>() ?? 0;

            using var request = kind switch
            {
                "advice" => Advice(outcome),
                "reversal" => Webhook(outcome, "Reversal", amount, sentAt),
                "refund" => Webhook(outcome, "Refund", amount, sentAt),
                _ => null,
            } ?? throw new ArgumentException($"Unknown callback '{item}'; use advice, reversal or refund[:amountMinor]");

            using var response = await http.SendAsync(request, cancellationToken);
            results.Add($"{item} -> {(int)response.StatusCode}");
        }

        return results;
    }

    private HttpRequestMessage Advice(JsonObject outcome)
    {
        var body = new JsonObject
        {
            ["RequestType"] = "Payment",
            ["MerchantID"] = settings.MerchantId,
            ["TransactionID"] = outcome["TransactionID"]?.GetValue<string>(),
            ["TransactionDateTime"] = outcome["TransactionDateTime"]?.GetValue<string>(),
            ["Amount"] = outcome["AuthAmount"]?.GetValue<long>() ?? 0,
            ["TransactionStatus"] = "Success",
            ["ResponseSource"] = "Acquirer",
            ["ResponseCode"] = "00",
            ["AuthCode"] = outcome["AuthCode"]?.GetValue<string>(),
            ["CommProtocol"] = "REST",
        }.ToJsonString();

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), "/acme/advice")) { Content = Json(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.AdviceUsername}:{settings.AdvicePassword}")));
        return request;
    }

    private HttpRequestMessage Webhook(JsonObject outcome, string type, long amount, string sentAt)
    {
        var transactionId = outcome["TransactionID"]!.GetValue<string>();
        var uuid = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{transactionId}|{type}|{amount}"))[..16]);
        var body = new JsonObject
        {
            ["transactionUUID"] = uuid.ToString(),
            ["transactionType"] = type,
            ["transactionDateTime"] = sentAt,
            ["rrn"] = outcome["RetrievalReferenceNumber"]?.GetValue<string>(),
            ["transactionAmount"] = amount,
            ["currencyCode"] = "ZAR",
            ["txResultCode"] = "00",
            ["isApproved"] = true,
        }.ToJsonString();

        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(settings.BaseUrl), "/acme/webhook")) { Content = Json(body) };
        var signature = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.WebhookSecret), Encoding.UTF8.GetBytes(body)));
        request.Headers.Add("x-signature", signature);
        return request;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}
