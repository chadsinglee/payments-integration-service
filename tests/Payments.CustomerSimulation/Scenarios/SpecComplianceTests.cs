using System.Net;
using Payments.CustomerSimulation.Support;
using Payments.CustomerSimulation.Traffic;

namespace Payments.CustomerSimulation.Scenarios;

/// <summary>Spec requirements that only show up in a running system: ordering, rate limits, public callbacks and secrets.</summary>
[Collection(StackCollection.Name)]
public sealed class SpecComplianceTests : IDisposable
{
    private const long SlowAmount = 99_035;
    private const string CardToken = "4f2a8c9e1b7d3560";

    private readonly Stack _stack = new();

    public void Dispose() => _stack.Dispose();

    // Task 1: "notifications may arrive out of order". Acme holds the slow payment for 35 s, so the webhook beats the approval.
    [Theory]
    [InlineData("Refund", 10_000, "PartiallyRefunded")]
    [InlineData("Reversal", SlowAmount, "Reversed")]
    public async Task Webhook_WhenItArrivesBeforeTheApproval_IsAppliedOnceTheApprovalLands(string type, long webhookAmount, string expected)
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-2);
        var paymentId = await _stack.StartPaymentAsync(SlowAmount);
        var rrn = await WaitForAcmePostAsync(since);

        var webhook = await _stack.PostWebhookAsync(Stack.WebhookBody(type, rrn, webhookAmount));

        webhook.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _stack.GetAsync(paymentId)).GetProperty("status").GetString().ShouldBeOneOf("Pending", "Submitted", "Approved", expected);
        var payment = await WaitForApprovalToLandAsync(paymentId);
        payment.GetProperty("status").GetString().ShouldBe(expected);
    }

    // The approval is what records the authorised amount, so this also keeps one slow payment from overlapping the next test.
    private async Task<System.Text.Json.JsonElement> WaitForApprovalToLandAsync(string paymentId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var payment = await _stack.GetAsync(paymentId);
            if (payment.GetProperty("authAmount").ValueKind == System.Text.Json.JsonValueKind.Number)
            {
                return payment;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"The approval for {paymentId} never landed.");
    }

    // Integration requirement: Acme allows 20 requests/s; a burst of customers must not exceed it or stall the API.
    [Fact]
    public async Task Burst_WhenOneHundredFiftyPaymentsArriveAtOnce_AcmeNeverSeesMoreThanTwentyPostsInASecond()
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-2);
        var amounts = Enumerable.Range(0, 150).Select(_ => Stack.NextAmount()).ToArray();

        var timed = await Task.WhenAll(amounts.Select(async amount =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var id = await _stack.StartPaymentAsync(amount);
            return (Id: id, Accepted: clock.Elapsed);
        }));
        var outcomes = await Task.WhenAll(timed.Select(t => _stack.WaitForOutcomeAsync(t.Id, TimeSpan.FromSeconds(120))));

        outcomes.Select(o => o.GetProperty("status").GetString()).ShouldAllBe(status => status == "Approved");
        timed.Max(t => t.Accepted).ShouldBeLessThan(TimeSpan.FromSeconds(5));
        var times = (await _stack.AcmePostsSinceAsync(since))
            .Where(post => amounts.Contains(post.GetProperty("amount").GetInt64()))
            .Select(post => post.GetProperty("receivedAt").GetDateTimeOffset())
            .Order()
            .ToArray();
        times.Length.ShouldBeGreaterThanOrEqualTo(150);
        // The 1 s peak is what Acme enforces; the sustained rate alone would hide a bunched-up burst.
        MaxInAnyWindow(times, TimeSpan.FromSeconds(1)).ShouldBeLessThanOrEqualTo(20);
    }

    // Spec: card tokens are stored, never returned; the response to the app must not leak them or the customer's email.
    [Fact]
    public async Task Responses_WhenPayingWithAStoredCard_NeverContainTheTokenOrEmail()
    {
        var paymentId = Stack.NewPaymentId();

        var submit = await (await _stack.SubmitAsync(paymentId, Stack.NextAmount())).Content.ReadAsStringAsync();
        await _stack.WaitForOutcomeAsync(paymentId);
        var query = await (await _stack.GetRawAsync($"/payments/{paymentId}")).Content.ReadAsStringAsync();

        foreach (var body in new[] { submit, query })
        {
            body.ShouldNotContain(CardToken);
            body.ShouldNotContain("thandi@example.com");
        }
    }

    // Spec: amounts are whole minor units.
    [Fact]
    public async Task Pay_WhenTheAmountIsFractional_Returns400()
    {
        var response = await _stack.PostRawPaymentAsync($$"""{"paymentId":"{{Stack.NewPaymentId()}}","customerId":"cust-001","subMerchantId":"ZAP0004182","amount":10.5}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // Spec: Acme resends unless it gets a 200, so valid notifications are acknowledged; extra fields are ignored.
    [Fact]
    public async Task Advice_WhenAuthenticatedForAnUnknownTransactionWithExtraFields_IsAcknowledged()
    {
        var body = $$$"""{"RequestType":"Payment","MerchantID":"{{{Stack.MerchantId}}}","TransactionID":"{{{Guid.NewGuid()}}}","TransactionStatus":"Success","ResponseCode":"00","FutureField":{"added":"by acme"}}""";

        var response = await _stack.PostAdviceAsync(Stack.AdviceCredentials, body);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Advice_WhenTheMerchantIsAnotherAccount_Returns403()
    {
        var body = $$"""{"RequestType":"Payment","MerchantID":"SOMEONE-ELSE","TransactionID":"{{Guid.NewGuid()}}","TransactionStatus":"Success","ResponseCode":"00"}""";

        var response = await _stack.PostAdviceAsync(Stack.AdviceCredentials, body);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Advice_WhenAuthenticatedButNotJson_Returns400()
    {
        var response = await _stack.PostAdviceAsync(Stack.AdviceCredentials, "not json");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Advice_WhenTheBodyIsOversized_Returns413()
    {
        var response = await _stack.PostAdviceAsync(Stack.AdviceCredentials, new string('x', 70_000));

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Webhook_WhenSignedForAnUnknownPaymentAndDeliveredTwice_IsAcknowledgedBothTimes()
    {
        var body = Stack.WebhookBody("Refund", "ZNOSUCHRRN1", 500);

        var first = await _stack.PostWebhookAsync(body);
        var second = await _stack.PostWebhookAsync(body);

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Spec: enough logging to troubleshoot, but never tokens or secrets. Needs the docker CLI.
    [OptInFact("SIM_DOCKER")]
    public async Task Logs_AfterPayments_NeverContainTheCardTokenOrAnySecret()
    {
        var paymentId = await _stack.StartPaymentAsync(Stack.NextAmount());
        await _stack.WaitForOutcomeAsync(paymentId);

        var logs = await DockerCompose.RunAsync("logs --no-color api worker");

        logs.ShouldContain("submitted as");
        foreach (var secret in new[] { CardToken, "sk_test_stub", "local-webhook-secret", "local-advice-password" })
        {
            logs.ShouldNotContain(secret);
        }
    }

    private async Task<string> WaitForAcmePostAsync(DateTimeOffset since)
    {
        for (var i = 0; i < 40; i++)
        {
            var posts = await _stack.AcmePostsForAmountAsync(SlowAmount, since);
            if (posts.Length > 0)
            {
                return posts[0].GetProperty("orderNumber").GetString()![..12];
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Acme never received the slow payment.");
    }

    private static int MaxInAnyWindow(DateTimeOffset[] sorted, TimeSpan window)
    {
        var max = 0;
        var start = 0;
        for (var end = 0; end < sorted.Length; end++)
        {
            while (sorted[end] - sorted[start] >= window)
            {
                start++;
            }

            max = Math.Max(max, end - start + 1);
        }

        return max;
    }
}
