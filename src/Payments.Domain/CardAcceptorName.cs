namespace Payments.Domain;

public static class CardAcceptorName
{
    public const int Length = 40;
    private const string FacilitatorPrefix = "ZAP*";
    private const int NameWidth = 23;
    private const int CityWidth = 13;

    public static string Format(string subMerchantName, string city, string regionCode, string countryCode)
    {
        if (string.IsNullOrWhiteSpace(subMerchantName)) throw new ArgumentException("Sub-merchant name is required.", nameof(subMerchantName));
        if (string.IsNullOrWhiteSpace(city)) throw new ArgumentException("City is required.", nameof(city));
        if (regionCode is null || regionCode.Length < 2) throw new ArgumentException("Region code needs at least 2 characters.", nameof(regionCode));
        if (countryCode is null || countryCode.Length != 2) throw new ArgumentException("Country code must be 2 characters.", nameof(countryCode));

        if (subMerchantName.Contains('*') || city.Contains('*')) throw new ArgumentException("Sub-merchant name and city must not contain '*'.");

        var name = Fit(FacilitatorPrefix + subMerchantName.Trim(), NameWidth);
        var cityPart = Fit(city.Trim(), CityWidth);
        var result = name + cityPart + regionCode[..2] + countryCode;

        return result.Length == Length ? result : throw new InvalidOperationException("CardAcceptorName must be exactly 40 characters.");
    }

    private static string Fit(string value, int width) =>
        (value.Length > width ? value[..width] : value).PadRight(width);
}
