using System.Net;
using Payments.CustomerSimulation.Support;

namespace Payments.CustomerSimulation.Scenarios;

[Collection(StackCollection.Name)]
public sealed class PayingTests : IDisposable
{
    private readonly Stack _stack = new();

    public void Dispose() => _stack.Dispose();

    [Fact]
    public async Task Pay_WhenAcmeApproves_EndsApprovedForTheFullAmount()
    {
        var amount = Stack.NextAmount();
        var paymentId = Stack.NewPaymentId();

        var response = await _stack.SubmitAsync(paymentId, amount);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var payment = await _stack.WaitForOutcomeAsync(paymentId);
        payment.GetProperty("status").GetString().ShouldBe("Approved");
        payment.GetProperty("authAmount").GetInt64().ShouldBe(amount);
        payment.GetProperty("currency").GetString().ShouldBe("ZAR");
    }

    [Theory]
    [InlineData(99_005)]
    [InlineData(99_051)]
    public async Task Pay_WhenTheCardIsDeclined_EndsDeclined(long declineAmount)
    {
        var paymentId = await _stack.StartPaymentAsync(declineAmount);

        var payment = await _stack.WaitForOutcomeAsync(paymentId);

        payment.GetProperty("status").GetString().ShouldBe("Declined");
    }

    [Fact]
    public async Task Pay_WhenTheAppRetriesTheSameRequest_ReturnsTheSamePaymentAndAcmeIsChargedOnce()
    {
        var amount = Stack.NextAmount();
        var paymentId = await _stack.StartPaymentAsync(amount);

        var retry = await _stack.SubmitAsync(paymentId, amount);

        retry.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _stack.WaitForOutcomeAsync(paymentId)).GetProperty("status").GetString().ShouldBe("Approved");
        (await _stack.AcmePostsForAmountAsync(amount)).Length.ShouldBe(1);
    }

    [Fact]
    public async Task Pay_WhenAPaymentIdIsReusedWithADifferentAmount_Returns409()
    {
        var amount = Stack.NextAmount();
        var paymentId = await _stack.StartPaymentAsync(amount);

        var response = await _stack.SubmitAsync(paymentId, amount + 1);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("", "cust-001", "ZAP0004182", 100, HttpStatusCode.BadRequest)]
    [InlineData("sim-bad", "cust-001", "ZAP0004182", 0, HttpStatusCode.BadRequest)]
    [InlineData("sim-bad", "cust-001", "ZAP0004182", -5, HttpStatusCode.BadRequest)]
    [InlineData("sim-bad", "no-such-customer", "ZAP0004182", 100, HttpStatusCode.NotFound)]
    [InlineData("sim-bad", "cust-001", "ZAP9999999", 100, HttpStatusCode.NotFound)]
    public async Task Pay_WhenTheRequestCannotBeAccepted_ReturnsTheExpectedError(string paymentId, string customerId, string subMerchantId, long amount, HttpStatusCode expected)
    {
        var response = await _stack.SubmitAsync(paymentId, amount, customerId, subMerchantId);

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task GetPayment_WhenTheIdIsUnknown_Returns404()
    {
        var response = await _stack.GetRawAsync("/payments/never-submitted");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pay_WhenTwentyCustomersPayAtOnce_EachIsApprovedAndChargedExactlyOnce()
    {
        var amounts = Enumerable.Range(0, 20).Select(_ => Stack.NextAmount()).ToArray();

        var paymentIds = await Task.WhenAll(amounts.Select(amount => _stack.StartPaymentAsync(amount)));
        var outcomes = await Task.WhenAll(paymentIds.Select(_stack.WaitForOutcomeAsync));

        outcomes.Select(outcome => outcome.GetProperty("status").GetString()).ShouldAllBe(status => status == "Approved");
        foreach (var amount in amounts)
        {
            (await _stack.AcmePostsForAmountAsync(amount)).Length.ShouldBe(1);
        }
    }
}
