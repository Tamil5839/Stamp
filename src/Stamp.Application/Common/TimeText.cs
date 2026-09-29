using System.Globalization;

namespace Stamp.Application.Common;

public static class TimeText
{
    /// <summary>"6 days", "1 day", "5 hours", "12 minutes".</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return Plural((int)Math.Round(duration.TotalDays), "day");
        }

        if (duration.TotalHours >= 1)
        {
            return Plural((int)Math.Round(duration.TotalHours), "hour");
        }

        return Plural(Math.Max(1, (int)Math.Round(duration.TotalMinutes)), "minute");
    }

    /// <summary>"Wed, Oct 7 at 14:00 UTC".</summary>
    public static string Moment(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("ddd, MMM d 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);

    private static string Plural(int count, string unit) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {unit}{(count == 1 ? string.Empty : "s")}");
}
