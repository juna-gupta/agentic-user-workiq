using System.Globalization;

namespace IncidentAgent.Agent;

/// <summary>Turns "tomorrow", "friday" or "2026-10-12" into a date in the team's time zone, without asking the model.</summary>
public static class DateResolver
{
    private static readonly string[] MonthDayFormats = ["MMM d", "MMMM d", "MMM d yyyy", "MMMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy", "M/d", "M/d/yyyy"];

    public static DateOnly? Resolve(string? when, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (string.IsNullOrWhiteSpace(when))
        {
            return null;
        }

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var s = when.Trim().ToLowerInvariant().TrimEnd('.', '?', '!');
        foreach (var filler in new[] { "on ", "for ", "the " })
        {
            if (s.StartsWith(filler, StringComparison.Ordinal))
            {
                s = s[filler.Length..];
            }
        }

        switch (s)
        {
            case "today" or "tonight" or "this morning" or "this afternoon" or "later today":
                return today;
            case "tomorrow" or "tomorrow's" or "tmrw":
                return today.AddDays(1);
            case "day after tomorrow":
                return today.AddDays(2);
        }

        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }

        // "friday", "this friday", "next friday": the coming one; the same weekday as today means a week out, unless "this".
        var thisWeek = s.StartsWith("this ", StringComparison.Ordinal);
        var named = s.Replace("next ", string.Empty).Replace("this ", string.Empty).Replace("'s", string.Empty).Trim();
        if (named.Length >= 3 && char.IsLetter(named[0]) && Enum.TryParse<DayOfWeek>(named, ignoreCase: true, out var weekday))
        {
            var ahead = ((int)weekday - (int)today.DayOfWeek + 7) % 7;
            return today.AddDays(ahead == 0 && !thisWeek ? 7 : ahead);
        }

        foreach (var format in MonthDayFormats)
        {
            if (DateTime.TryParseExact(named, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
            {
                var day = DateOnly.FromDateTime(parsed);
                if (!format.Contains('y') && day.AddDays(1) < today)
                {
                    day = day.AddYears(1);
                }

                return day;
            }
        }

        return null;
    }
}
