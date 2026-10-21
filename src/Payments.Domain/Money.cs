namespace Payments.Domain;

public readonly record struct Money
{
    public const string Currency = "ZAR";

    public long MinorUnits { get; }

    private Money(long minorUnits) => MinorUnits = minorUnits;

    public static Money FromMinorUnits(long minorUnits) =>
        minorUnits > 0
            ? new Money(minorUnits)
            : throw new ArgumentOutOfRangeException(nameof(minorUnits), "Amount must be positive.");

    public Money Add(Money other) => new(checked(MinorUnits + other.MinorUnits));
}
