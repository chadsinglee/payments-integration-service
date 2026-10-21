using Payments.Domain;

namespace Payments.UnitTests.Domain;

public class DomainBasicsTests
{
    [Fact]
    public void Format_WhenValidParts_ReturnsExactly40CharsByPosition()
    {
        var value = CardAcceptorName.Format("CAFE DEL SOL", "CAPE TOWN", "WC", "ZA");

        value.Length.ShouldBe(40);
        value[3].ShouldBe('*');
        value[..23].ShouldBe("ZAP*CAFE DEL SOL       ");
        value.Substring(23, 13).ShouldBe("CAPE TOWN    ");
        value.Substring(36, 2).ShouldBe("WC");
        value.Substring(38, 2).ShouldBe("ZA");
    }

    [Fact]
    public void Format_WhenNameAndCityTooLong_TruncatesAndTakesFirstTwoRegionChars()
    {
        var value = CardAcceptorName.Format("A VERY LONG SUB MERCHANT NAME", "JOHANNESBURG NORTH", "GAU", "ZA");

        value.Length.ShouldBe(40);
        value[..23].ShouldBe("ZAP*A VERY LONG SUB MER");
        value.Substring(23, 13).ShouldBe("JOHANNESBURG ");
        value.Substring(36, 2).ShouldBe("GA");
    }

    [Theory]
    [InlineData("", "CAPE TOWN", "WC", "ZA")]
    [InlineData("CAFE", "", "WC", "ZA")]
    [InlineData("CAFE", "CAPE TOWN", "W", "ZA")]
    [InlineData("CAFE", "CAPE TOWN", "WC", "ZAF")]
    [InlineData("BOB*S GRILL", "CAPE TOWN", "WC", "ZA")]
    [InlineData("CAFE", "CAPE*TOWN", "WC", "ZA")]
    public void Format_WhenPartInvalid_Throws(string name, string city, string region, string country) =>
        Should.Throw<ArgumentException>(() => CardAcceptorName.Format(name, city, region, country));

    [Theory]
    [InlineData(1, "Z00000000001")]
    [InlineData(99_999_999_999, "Z99999999999")]
    public void FromSequence_WhenInRange_ReturnsZPlus11DigitsIn12Chars(long sequence, string expected)
    {
        var value = OrderNumber.FromSequence(sequence);

        value.ShouldBe(expected);
        value.Length.ShouldBe(OrderNumber.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100_000_000_000)]
    public void FromSequence_WhenOutOfRange_Throws(long sequence) =>
        Should.Throw<ArgumentOutOfRangeException>(() => OrderNumber.FromSequence(sequence));

    [Fact]
    public void FormatMessageDateTime_WhenUtcInstant_ReturnsUtcPlus2WithoutZone()
    {
        var utc = new DateTimeOffset(2026, 3, 1, 11, 56, 37, 104, TimeSpan.Zero);

        Sast.FormatMessageDateTime(utc).ShouldBe("2026-03-01T13:56:37.104");
    }

    [Fact]
    public void ParseUnzoned_WhenSastLocalValue_ReturnsInstantAtUtcPlus2()
    {
        var instant = Sast.ParseUnzoned("2026-03-01T13:56:37.104");

        instant.UtcDateTime.ShouldBe(new DateTime(2026, 3, 1, 11, 56, 37, 104, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(21, 59, 1)]
    [InlineData(22, 0, 2)]
    public void BusinessDate_WhenAroundUtc2200_RollsOverToNextSastDay(int hour, int minute, int expectedDay)
    {
        var instant = new DateTimeOffset(2026, 3, 1, hour, minute, 0, TimeSpan.Zero);

        Sast.BusinessDate(instant).ShouldBe(new DateOnly(2026, 3, expectedDay));
    }

    [Fact]
    public void FromMinorUnits_WhenZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Money.FromMinorUnits(0));

    [Fact]
    public void Add_WhenTwoAmounts_SumsMinorUnits() =>
        Money.FromMinorUnits(150).Add(Money.FromMinorUnits(50)).MinorUnits.ShouldBe(200);

    [Fact]
    public void Currency_Always_IsZar() => Money.Currency.ShouldBe("ZAR");
}
