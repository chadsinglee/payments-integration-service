using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Payments.CustomerSimulation.Support;

/// <summary>Talks to the running Payments API and Acme stub over HTTP, as the Zapper app and Acme would.</summary>
public sealed class Stack : IDisposable
{
    public const string Cafe = "ZAP0004182";
    public const string KotaKing = "ZAP0004183";
    public const string Customer = "cust-001";

    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);
    private static long _amountSeed = Random.Shared.NextInt64(10_000, 90_000);

    private readonly HttpClient _api = new() { BaseAddress = new Uri(Env("PAYMENTS_API_URL", "http://localhost:5001")) };
    private readonly HttpClient _stub = new() { BaseAddress = new Uri(Env("ACME_STUB_URL", "http://localhost:5002")) };
    private readonly string _webhookSecret = Env("ACME_WEBHOOK_SECRET", "local-webhook-secret");

    public static string AdviceCredentials { get; } = $"{Env("ACME_ADVICE_USERNAME", "local-advice-user")}:{Env("ACME_ADVICE_PASSWORD", "local-advice-password")}";

    public static string MerchantId { get; } = Env("ACME_MERCHANT_ID", "MID-LOCAL");

    public static string SastNow() => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-ddTHH:mm:ss.fff");

    /// <summary>A Transaction Status webhook body for the payment whose Retrieval Reference Number is <paramref name="rrn"/>.</summary>
    public static string WebhookBody(string type, string rrn, long amount) =>
        $$"""{"transactionUUID":"{{Guid.NewGuid()}}","transactionType":"{{type}}","transactionDateTime":"{{SastNow()}}","rrn":"{{rrn}}","transactionAmount":{{amount}},"currencyCode":"ZAR","txResultCode":"00","isApproved":true}""";

    public async Task<HttpResponseMessage> PostRawPaymentAsync(string json) =>
        await _api.PostAsync("/payments", new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>A fresh amount in minor units that is not one of the stub's magic amounts (those start at 99,000).</summary>
    public static long NextAmount() => Interlocked.Increment(ref _amountSeed) % 80_000 + 1_000;

    /// <summary>Unique multiple of four (so quarter and half refunds are whole minor units) above every other generated amount.</summary>
    public static long NextRefundableAmount() => NextAmount() * 4 + 100_000;

    public static string NewPaymentId() => $"sim-{Guid.NewGuid():N}"[..24];

    public async Task<HttpResponseMessage> SubmitAsync(string paymentId, long amount, string customerId = Customer, string subMerchantId = Cafe) =>
        await _api.PostAsJsonAsync("/payments", new { paymentId, customerId, subMerchantId, amount });

    public async Task<JsonElement> GetAsync(string paymentId) => await _api.GetFromJsonAsync<JsonElement>($"/payments/{paymentId}");

    public async Task<HttpResponseMessage> GetRawAsync(string path) => await _api.GetAsync(path);

    /// <summary>Submits a payment and returns its id once the API has accepted it.</summary>
    public async Task<string> StartPaymentAsync(long amount, string subMerchantId = Cafe)
    {
        var paymentId = NewPaymentId();
        var response = await SubmitAsync(paymentId, amount, subMerchantId: subMerchantId);
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.Accepted);
        return paymentId;
    }

    /// <summary>Polls like the app would until the payment leaves the in-flight states.</summary>
    public Task<JsonElement> WaitForOutcomeAsync(string paymentId) =>
        WaitForAsync(paymentId, status => status is not ("Pending" or "Submitted"));

    public Task<JsonElement> WaitForOutcomeAsync(string paymentId, TimeSpan limit) =>
        WaitForAsync(paymentId, status => status is not ("Pending" or "Submitted"), limit);

    public async Task<JsonElement> GetSummaryAsync(string day) =>
        (await _api.GetFromJsonAsync<JsonElement>($"/summary?date={day}")).Clone();

    public async Task<JsonElement[]> AcmePostsSinceAsync(DateTimeOffset since)
    {
        var posts = await _stub.GetFromJsonAsync<JsonElement[]>("/admin/posts");
        return posts!.Where(post => post.GetProperty("receivedAt").GetDateTimeOffset() >= since).ToArray();
    }

    public async Task<JsonElement> WaitForAsync(string paymentId, Func<string, bool> done, TimeSpan? limit = null)
    {
        var deadline = DateTime.UtcNow + (limit ?? WaitLimit);
        JsonElement payment;
        while (true)
        {
            payment = await GetAsync(paymentId);
            if (done(payment.GetProperty("status").GetString()!) || DateTime.UtcNow > deadline)
            {
                return payment;
            }

            await Task.Delay(250);
        }
    }

    /// <summary>What Acme received for this amount since <paramref name="since"/>; the stub keeps posts from earlier runs.</summary>
    public async Task<JsonElement[]> AcmePostsForAmountAsync(long amount, DateTimeOffset? since = null)
    {
        var posts = await _stub.GetFromJsonAsync<JsonElement[]>("/admin/posts");
        return posts!
            .Where(post => post.GetProperty("amount").GetInt64() == amount && post.GetProperty("receivedAt").GetDateTimeOffset() >= (since ?? DateTimeOffset.MinValue))
            .ToArray();
    }

    /// <summary>Acme sends callbacks for a payment it processed. The script is e.g. "advice", "refund:500", "reversal".</summary>
    public async Task<string[]> AcmeSendsAsync(long amount, string script, DateTimeOffset since)
    {
        var transactionId = (await AcmePostsForAmountAsync(amount, since)).Select(post => post.GetProperty("transactionId").GetString()).Distinct().Single();
        var response = await _stub.PostAsync($"/admin/callbacks/{transactionId}?send={Uri.EscapeDataString(script)}", null);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<string[]>())!;
    }

    public async Task<HttpResponseMessage> PostAdviceAsync(string? basicCredentials, string body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acme/advice") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (basicCredentials is not null)
        {
            request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basicCredentials)));
        }

        return await _api.SendAsync(request);
    }

    public async Task<HttpResponseMessage> PostWebhookAsync(string body, string? signature = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/acme/webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("x-signature", signature ?? Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_webhookSecret), Encoding.UTF8.GetBytes(body))));
        return await _api.SendAsync(request);
    }

    public void Dispose()
    {
        _api.Dispose();
        _stub.Dispose();
    }

    private static string Env(string name, string fallback) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : fallback;
}
