namespace Payments.Domain;

public static class OrderNumber
{
    public const int Length = 12;
    private const long MaxSequence = 99_999_999_999;

    public static string FromSequence(long sequence) =>
        sequence is < 1 or > MaxSequence
            ? throw new ArgumentOutOfRangeException(nameof(sequence))
            : "Z" + sequence.ToString("D11", System.Globalization.CultureInfo.InvariantCulture);
}
