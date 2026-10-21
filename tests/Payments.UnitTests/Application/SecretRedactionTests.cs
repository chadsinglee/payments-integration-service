using Payments.Application.Payments;
using Payments.Infrastructure.Acme;
using Payments.Infrastructure.Persistence;

namespace Payments.UnitTests.Application;

public class SecretRedactionTests
{
    private const string CardToken = "4f2a8c9e1b7d3560";
    private const string Email = "thandi@example.com";

    [Fact]
    public void Records_WhenFormatted_NeverShowTokensEmailsOrKeys()
    {
        var merchant = new SubMerchant("ZAP0004182", "Cafe", "Cape Town", "WC", "ZA", "5812");
        var printed = new object[]
        {
            new StoredCard(1, "cust-001", CardToken, Email),
            new ClaimedPayment(Guid.NewGuid(), Guid.NewGuid(), "000000000001", 100, 1, CardToken, Email, merchant),
            new AcmePaymentRequest(Guid.NewGuid(), Guid.NewGuid(), "000000000001", 100, CardToken, Email, "ZAP0004182", "5812", "CAFE*CAPE TOWN"),
            new AcmeOptions(new Uri("http://acme/"), "sk_test_secret", "MID", TimeSpan.FromSeconds(40)),
            new DatabaseSettings("Host=db;Password=hunter2", true, false),
            new RedisRateLimiterOptions("redis:6379,password=hunter2", 18),
        }.Select(value => value.ToString()!).ToList();

        foreach (var text in printed)
        {
            text.ShouldNotContain(CardToken);
            text.ShouldNotContain(Email);
            text.ShouldNotContain("sk_test_secret");
            text.ShouldNotContain("hunter2");
        }
    }
}
