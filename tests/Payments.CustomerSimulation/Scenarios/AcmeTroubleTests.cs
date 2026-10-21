using Payments.CustomerSimulation.Support;

namespace Payments.CustomerSimulation.Scenarios;

[Collection(StackCollection.Name)]
public sealed class AcmeTroubleTests : IDisposable
{
    private readonly Stack _stack = new();

    public void Dispose() => _stack.Dispose();

    [Fact]
    public async Task Pay_WhenAcmeGatewayIsDown_EndsUnknownNotFailed()
    {
        var paymentId = await _stack.StartPaymentAsync(99_503);

        var payment = await _stack.WaitForOutcomeAsync(paymentId);

        payment.GetProperty("status").GetString().ShouldBe("Unknown");
    }

    // Recovery waits for the worker's reconciliation backoff (10 s base), so allow longer than the default.
    [Theory]
    [InlineData(99_502)]
    [InlineData(99_429)]
    public async Task Pay_WhenAcmeFailsOnceThenRecovers_EndsApprovedUnderOneTransactionId(long amount)
    {
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);
        var paymentId = await _stack.StartPaymentAsync(amount);

        var payment = await _stack.WaitForAsync(paymentId, status => status == "Approved", TimeSpan.FromSeconds(120));

        payment.GetProperty("status").GetString().ShouldBe("Approved");
        var posts = await _stack.AcmePostsForAmountAsync(amount, since);
        posts.Select(post => post.GetProperty("transactionId").GetString()).Distinct().Count().ShouldBe(1);
    }
}
