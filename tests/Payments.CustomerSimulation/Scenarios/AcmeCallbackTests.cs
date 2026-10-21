using System.Net;
using Payments.CustomerSimulation.Support;

namespace Payments.CustomerSimulation.Scenarios;

[Collection(StackCollection.Name)]
public sealed class AcmeCallbackTests : IDisposable
{
    private readonly Stack _stack = new();

    public void Dispose() => _stack.Dispose();

    [Fact]
    public async Task Refund_WhenPartialThenRemainder_MovesToPartiallyRefundedThenRefunded()
    {
        var (paymentId, amount, since) = await ApprovedPaymentAsync();

        await _stack.AcmeSendsAsync(amount, $"refund:{amount / 4}", since);
        (await _stack.GetAsync(paymentId)).GetProperty("status").GetString().ShouldBe("PartiallyRefunded");

        await _stack.AcmeSendsAsync(amount, $"refund:{amount - amount / 4}", since);
        (await _stack.GetAsync(paymentId)).GetProperty("status").GetString().ShouldBe("Refunded");
    }

    [Fact]
    public async Task Refund_WhenAcmeDeliversTheSameRefundTwice_AppliesItOnce()
    {
        var (paymentId, amount, since) = await ApprovedPaymentAsync();

        await _stack.AcmeSendsAsync(amount, $"refund:{amount / 2},refund:{amount / 2}", since);

        (await _stack.GetAsync(paymentId)).GetProperty("status").GetString().ShouldBe("PartiallyRefunded");
    }

    [Fact]
    public async Task Reversal_WhenAcmeReversesAnApprovedPayment_EndsReversed()
    {
        var (paymentId, amount, since) = await ApprovedPaymentAsync();

        await _stack.AcmeSendsAsync(amount, "reversal", since);

        (await _stack.GetAsync(paymentId)).GetProperty("status").GetString().ShouldBe("Reversed");
    }

    [Fact]
    public async Task Advice_WhenAcmeRepeatsTheApprovalAfterARefund_DoesNotUndoTheRefund()
    {
        var (paymentId, amount, since) = await ApprovedPaymentAsync();
        await _stack.AcmeSendsAsync(amount, $"refund:{amount / 2}", since);

        await _stack.AcmeSendsAsync(amount, "advice", since);

        (await _stack.GetAsync(paymentId)).GetProperty("status").GetString().ShouldBe("PartiallyRefunded");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("local-advice-user:wrong-password")]
    public async Task Advice_WhenCredentialsAreMissingOrWrong_Returns401(string? credentials)
    {
        var response = await _stack.PostAdviceAsync(credentials, """{"TransactionID":"00000000-0000-0000-0000-000000000000"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Webhook_WhenTheSignatureIsForged_Returns401AndTheRefundIsNotApplied()
    {
        var (paymentId, amount, since) = await ApprovedPaymentAsync();
        var rrn = (await _stack.AcmePostsForAmountAsync(amount, since)).Single().GetProperty("orderNumber").GetString()![..12];
        var body = $$"""{"transactionUUID":"{{Guid.NewGuid()}}","transactionType":"Refund","rrn":"{{rrn}}","txResultCode":"00","isApproved":true,"transactionAmount":{{amount}}}""";

        var response = await _stack.PostWebhookAsync(body, signature: Convert.ToBase64String(new byte[32]));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _stack.GetAsync(paymentId)).GetProperty("status").GetString().ShouldBe("Approved");
    }

    [Fact]
    public async Task Webhook_WhenSignedCorrectlyButMissingFields_Returns400()
    {
        var response = await _stack.PostWebhookAsync("""{"transactionType":"Refund"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // Multiples of four so half and quarter refunds stay whole minor units (and never hit an odd-valued magic amount).
    private async Task<(string PaymentId, long Amount, DateTimeOffset Since)> ApprovedPaymentAsync()
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-5);
        var amount = Stack.NextAmount() * 4;
        var paymentId = await _stack.StartPaymentAsync(amount);
        (await _stack.WaitForOutcomeAsync(paymentId)).GetProperty("status").GetString().ShouldBe("Approved");
        return (paymentId, amount, since);
    }
}
