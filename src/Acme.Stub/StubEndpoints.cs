using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Acme.Stub;

/// <summary>Marker for WebApplicationFactory.</summary>
public sealed class StubAssemblyMarker;

public sealed record ReceivedPost(string TransactionId, string MessageId, string OrderNumber, long Amount, DateTimeOffset ReceivedAt);

public sealed class StubState
{
    public const long GatewayErrorAmount = 99_503;
    public const long ForeignRrnAmount = 99_409;
    public const long ApplicationFailureAmount = 99_422;

    // First POST per TransactionID fails with 503 and is not stored; later POSTs behave normally (RTTQ 404 then resend).
    public const long FirstPostFailsAmount = 99_502;

    // First POST per TransactionID is rate limited (429/E29, Retry-After 1) and not stored; later POSTs behave normally.
    public const long FirstPostRateLimitedAmount = 99_429;

    // Acme can hold a request for up to 30 s; this answers normally after SlowDelay (default 35 s, above the client timeout's 30 s floor).
    public const long SlowAmount = 99_035;

    public TimeSpan SlowDelay { get; set; } = TimeSpan.FromSeconds(35);

    public ConcurrentDictionary<string, bool> RateLimitedFirstPosts { get; } = new();

    // Magic amounts for acquirer declines.
    public static readonly IReadOnlyDictionary<long, (string Code, string Description)> DeclineCodes = new Dictionary<long, (string, string)>
    {
        [99_005] = ("05", "Do not honour"),
        [99_051] = ("51", "Insufficient funds"),
    };

    public ConcurrentQueue<ReceivedPost> Posts { get; } = new();

    // TransactionID -> stored response (Acme replays the stored outcome for 24 h).
    public ConcurrentDictionary<string, (int Status, JsonObject Payment)> Outcomes { get; } = new();

    // First 12 chars of OrderNumber -> owning TransactionID.
    public ConcurrentDictionary<string, string> Rrns { get; } = new();

    public ConcurrentDictionary<string, bool> FailedFirstPosts { get; } = new();

    // TransactionID -> number of RTTQ answers that still report Processing.
    public ConcurrentDictionary<string, int> ProcessingCounts { get; } = new();

    // TransactionID -> TransactionStatus RTTQ reports instead of the stored one (Reversed, Refunded, Complete).
    public ConcurrentDictionary<string, string> StatusOverrides { get; } = new();
}

public static class StubEndpoints
{
    private static readonly string[] RequiredFields = ["MerchantID", "TransactionID", "OrderNumber", "Amount", "PaymentService"];

    public static void MapAcmeStub(this WebApplication app)
    {
        var state = app.Services.GetRequiredService<StubState>();
        var apiKey = app.Configuration["Stub:ApiKey"] ?? "sk_test_stub";

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
        app.MapGet("/admin/posts", () => Results.Ok(state.Posts));
        app.MapPost("/api/payment", async (HttpRequest request) => await HandlePaymentAsync(request, state, apiKey));
        app.MapGet("/apirttq/query/Merchant/{merchantId}", (HttpRequest request, string merchantId) => HandleQuery(request, state, apiKey));
        app.MapPost("/admin/rttq-processing/{transactionId}", (string transactionId, int count) =>
        {
            state.ProcessingCounts[transactionId] = count;
            return Results.NoContent();
        });
        app.MapPost("/admin/callbacks/{transactionId}", async (string transactionId, string send, CancellationToken cancellationToken) =>
        {
            var settings = StubCallbackSettings.From(app.Configuration);
            if (settings is null)
            {
                return Results.Problem("Set Stub__CallbackBaseUrl to the Api base URL to emit callbacks.", statusCode: 409);
            }

            if (!state.Outcomes.TryGetValue(transactionId, out var stored))
            {
                return Results.NotFound();
            }

            try
            {
                using var http = new HttpClient();
                return Results.Ok(await new CallbackEmitter(http, settings).SendAsync(stored.Payment, send, cancellationToken));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(ex.Message);
            }
        });
        app.MapPost("/admin/outcomes/{transactionId}/status", (string transactionId, string value) =>
        {
            state.StatusOverrides[transactionId] = value;
            return Results.NoContent();
        });
    }

    private static IResult HandleQuery(HttpRequest request, StubState state, string apiKey)
    {
        if (request.Headers.Authorization != $"Bearer {apiKey}")
        {
            return Envelope(401, Failure(null, null, "E02", "API key is missing or not valid"));
        }

        var transactionId = request.Query["TransactionID"].ToString();
        var orderNumber = request.Query["OrderNumber"].ToString();
        if (transactionId.Length == 0 && orderNumber.Length > 0)
        {
            transactionId = state.Outcomes.FirstOrDefault(o => o.Value.Payment["OrderNumber"]?.GetValue<string>() == orderNumber).Key ?? "";
        }

        if (!state.Outcomes.TryGetValue(transactionId, out var stored))
        {
            return Envelope(404, Failure(transactionId.Length == 0 ? null : transactionId, null, "E04", "Transaction not found"));
        }

        var payment = stored.Payment.DeepClone().AsObject();
        if (state.ProcessingCounts.TryGetValue(transactionId, out var remaining) && remaining > 0)
        {
            state.ProcessingCounts[transactionId] = remaining - 1;
            payment["TransactionStatus"] = "Processing";
        }
        else if (state.StatusOverrides.TryGetValue(transactionId, out var overridden))
        {
            payment["TransactionStatus"] = overridden;
        }

        return Envelope(stored.Status, payment);
    }

    private static async Task<IResult> HandlePaymentAsync(HttpRequest request, StubState state, string apiKey)
    {
        if (request.Headers.Authorization != $"Bearer {apiKey}")
        {
            return Envelope(401, Failure(null, null, "E02", "API key is missing or not valid"));
        }

        JsonObject? payment;
        var messageId = "";
        try
        {
            var root = await JsonNode.ParseAsync(request.Body) as JsonObject;
            payment = root?["PaymentRequest"] as JsonObject;
            messageId = root?["MessageHeader"]?["MessageID"]?.GetValue<string>() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            payment = null;
        }

        if (payment is null || RequiredFields.Any(field => payment[field] is null))
        {
            return Envelope(400, Failure(null, null, "E01", "The body could not be parsed or a required field is missing"));
        }

        var transactionId = payment["TransactionID"]!.GetValue<string>();
        var orderNumber = payment["OrderNumber"]!.GetValue<string>();
        var amount = payment["Amount"]!.GetValue<long>();
        state.Posts.Enqueue(new ReceivedPost(transactionId, messageId, orderNumber, amount, DateTimeOffset.UtcNow));

        if (state.Outcomes.TryGetValue(transactionId, out var stored))
        {
            return Envelope(stored.Status, stored.Payment.DeepClone().AsObject());
        }

        var rrn = orderNumber[..Math.Min(12, orderNumber.Length)];
        var owner = state.Rrns.GetOrAdd(rrn, transactionId);
        if (owner != transactionId)
        {
            return Envelope(409, Failure(owner, orderNumber, "E09", "Retrieval Reference Number already in use"));
        }

        if (amount == StubState.GatewayErrorAmount)
        {
            state.Rrns.TryRemove(rrn, out _);
            return Envelope(503, Failure(transactionId, orderNumber, "E50", "The gateway is degraded"));
        }

        if (amount == StubState.FirstPostFailsAmount && state.FailedFirstPosts.TryAdd(transactionId, true))
        {
            state.Rrns.TryRemove(rrn, out _);
            return Envelope(503, Failure(transactionId, orderNumber, "E50", "The gateway is degraded"));
        }

        if (amount == StubState.FirstPostRateLimitedAmount && state.RateLimitedFirstPosts.TryAdd(transactionId, true))
        {
            state.Rrns.TryRemove(rrn, out _);
            request.HttpContext.Response.Headers.RetryAfter = "1";
            return Envelope(429, Failure(transactionId, orderNumber, "E29", "You have exceeded 20 requests per second"));
        }

        if (amount == StubState.ForeignRrnAmount)
        {
            state.Rrns.TryRemove(rrn, out _);
            return Envelope(409, Failure(Guid.NewGuid().ToString(), orderNumber, "E09", "Retrieval Reference Number already in use"));
        }

        if (amount == StubState.ApplicationFailureAmount)
        {
            return Store(state, transactionId, 200, Failure(transactionId, orderNumber, "E22", "Stub application failure"));
        }

        if (StubState.DeclineCodes.TryGetValue(amount, out var decline))
        {
            return Store(state, transactionId, 200, Detailed(transactionId, orderNumber, "Success", Detail("Acquirer", decline.Code, decline.Description, "Declined.")));
        }

        if (amount == StubState.SlowAmount)
        {
            // Acme keeps processing after the caller gave up, so the delay ignores the client disconnecting.
            await Task.Delay(state.SlowDelay);
        }

        var approved = new JsonObject
        {
            ["TransactionID"] = transactionId,
            ["TransactionDateTime"] = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-ddTHH:mm:ss.fff"),
            ["OrderNumber"] = orderNumber,
            ["ReconID"] = "R" + Random.Shared.NextInt64(100_000_000, 999_999_999),
            ["RetrievalReferenceNumber"] = rrn,
            ["AuthCode"] = Random.Shared.Next(100_000, 999_999).ToString(),
            ["TransactionStatus"] = "Success",
            ["ResponseDetail"] = Detail("Acquirer", "00", "Approved or completed successfully", "Approved."),
            ["AuthAmount"] = amount,
        };
        state.Outcomes[transactionId] = (200, approved);
        return Envelope(200, approved);
    }

    private static IResult Store(StubState state, string transactionId, int status, JsonObject payment)
    {
        state.Outcomes[transactionId] = (status, payment);
        return Envelope(status, payment);
    }

    private static JsonObject Detailed(string transactionId, string orderNumber, string transactionStatus, JsonObject detail) =>
        new()
        {
            ["TransactionID"] = transactionId,
            ["OrderNumber"] = orderNumber,
            ["TransactionStatus"] = transactionStatus,
            ["ResponseDetail"] = detail,
        };

    private static IResult Envelope(int status, JsonObject payment)
    {
        var body = new JsonObject
        {
            ["MessageHeader"] = new JsonObject
            {
                ["MessageDateTime"] = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-ddTHH:mm:ss.fff"),
                ["MessageID"] = Guid.NewGuid().ToString(),
                ["Channel"] = "App",
            },
            ["PaymentResponse"] = payment,
        };
        return Results.Text(body.ToJsonString(), "application/json", statusCode: status);
    }

    private static JsonObject Failure(string? transactionId, string? orderNumber, string code, string description) =>
        new()
        {
            ["TransactionID"] = transactionId,
            ["OrderNumber"] = orderNumber,
            ["TransactionStatus"] = "Failure",
            ["ResponseDetail"] = Detail("Application", code, description, "We could not process that payment."),
        };

    private static JsonObject Detail(string source, string code, string description, string clientMessage) =>
        new() { ["Source"] = source, ["Code"] = code, ["Description"] = description, ["ClientMessage"] = clientMessage };
}
