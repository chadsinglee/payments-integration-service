using System.Globalization;

namespace Payments.Domain;

public static class Sast
{
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);
    public const string MessageDateTimeFormat = "yyyy-MM-ddTHH:mm:ss.fff";

    public static string FormatMessageDateTime(DateTimeOffset instant) =>
        instant.ToOffset(Offset).ToString(MessageDateTimeFormat, CultureInfo.InvariantCulture);

    public static DateTimeOffset ParseUnzoned(string sastLocal) =>
        new(DateTime.ParseExact(sastLocal, MessageDateTimeFormat, CultureInfo.InvariantCulture), Offset);

    /// <summary>Tolerant variant for inbound callbacks: an unparseable timestamp must not reject the notification.</summary>
    public static DateTimeOffset? TryParseUnzoned(string? sastLocal) =>
        DateTime.TryParseExact(sastLocal, UnzonedFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? new DateTimeOffset(parsed, Offset)
            : null;

    private static readonly string[] UnzonedFormats = ["yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss"];

    public static DateOnly BusinessDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);
}
