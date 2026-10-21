using Microsoft.Extensions.Configuration;
using Payments.Infrastructure.Acme;

namespace Payments.UnitTests.Acme;

public class RedisRateLimiterConfigurationTests
{
    private static IConfiguration Config(string? limit) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis"] = "redis:6379",
                ["Acme:RateLimitPerSecond"] = limit,
            })
            .Build();

    [Fact]
    public void Read_WhenNothingIsConfigured_DefaultsTo15()
    {
        RedisRateLimiterConfiguration.Read(Config(null)).LimitPerSecond.ShouldBe(15);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("19", 19)]
    public void Read_WhenTheLimitIsWithinTheCap_UsesIt(string value, int expected)
    {
        RedisRateLimiterConfiguration.Read(Config(value)).LimitPerSecond.ShouldBe(expected);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("20")]
    [InlineData("100")]
    public void Read_WhenTheLimitIsOutsideTheCap_FailsFast(string value)
    {
        Should.Throw<InvalidOperationException>(() => RedisRateLimiterConfiguration.Read(Config(value)))
            .Message.ShouldContain("between 1 and 19");
    }

    [Fact]
    public void ToString_NeverIncludesTheConnectionString()
    {
        new RedisRateLimiterOptions("redis:6379,password=hunter2", 18).ToString().ShouldNotContain("hunter2");
    }
}
