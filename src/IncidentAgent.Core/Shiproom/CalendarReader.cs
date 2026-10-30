using System.Globalization;
using System.Text.Json;
using IncidentAgent.Text;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentAgent.Shiproom;

public sealed class ShiproomOptions
{
    public const string Section = "Shiproom";

    /// <summary>Used when nobody names the meeting: "an update for tomorrow" means the next meeting with this in its subject.</summary>
    public string MeetingKeyword { get; set; } = "shiproom";

    /// <summary>How far ahead to look when no day is given.</summary>
    public int LookAheadDays { get; set; } = 7;

    /// <summary>The deck slide that incident updates go on; its contents in the previous deck are the template.</summary>
    public string SlideTitle { get; set; } = "Incident updates";
}

public sealed record Person(string Name, string? Email);

public sealed record Meeting(
    string Id,
    string Subject,
    DateTimeOffset Start,
    DateTimeOffset End,
    Person? Organizer,
    IReadOnlyList<Person> Attendees,
    IReadOnlyList<string> Links,
    string? WebLink);

/// <summary>Finds a meeting on the agent user's own calendar. It sees the meetings it was invited to, like anyone else.</summary>
public sealed class CalendarReader(IOptions<ShiproomOptions> options, TimeProvider clock, ILogger<CalendarReader> logger)
{
    /// <param name="day">The day to look on, or null for the next <see cref="ShiproomOptions.LookAheadDays"/> days.</param>
    public async Task<Meeting?> FindAsync(IWorkIq workIq, string keyword, DateOnly? day, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var (start, end) = day is { } d
            ? (StartOfDay(d, zone), StartOfDay(d.AddDays(1), zone))
            : (now, now.AddDays(options.Value.LookAheadDays));

        var page = (await workIq.FetchAsync(WorkIqPaths.CalendarView(start, end), cancellationToken)).EnsureSuccess();
        var meetings = page.Items.Select(Parse).OfType<Meeting>().ToList();
        var match = meetings.Where(m => Matches(m.Subject, keyword)).OrderBy(m => m.Start).FirstOrDefault();
        logger.LogInformation("Calendar {Start:u}..{End:u}: {Count} meetings, '{Keyword}' {Result}",
            start, end, meetings.Count, keyword, match is null ? "not found" : $"found at {match.Start:u}");
        return match;
    }

    public static DateTimeOffset StartOfDay(DateOnly day, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    /// <summary>Every word of the keyword appears in the subject, ignoring case.</summary>
    public static bool Matches(string subject, string keyword) =>
        keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(w => subject.Contains(w, StringComparison.OrdinalIgnoreCase));

    internal static Meeting? Parse(JsonElement ev)
    {
        if (ev.TryGetProperty("isCancelled", out var cancelled) && cancelled.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (Str(ev, "id") is not { } id || GraphTime(ev, "start") is not { } start || GraphTime(ev, "end") is not { } end)
        {
            return null;
        }

        var attendees = new List<Person>();
        if (ev.TryGetProperty("attendees", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in list.EnumerateArray())
            {
                if (Email(a) is { } p)
                {
                    attendees.Add(p);
                }
            }
        }

        var body = ev.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.Object ? Str(b, "content") : null;
        return new Meeting(
            id,
            Str(ev, "subject") ?? "(no subject)",
            start,
            end,
            ev.TryGetProperty("organizer", out var o) ? Email(o) : null,
            attendees,
            HtmlText.Links(body),
            Str(ev, "webLink"));
    }

    /// <summary>Event times come back as <c>{"dateTime":"2026-10-11T17:00:00.0000000","timeZone":"UTC"}</c>.</summary>
    private static DateTimeOffset? GraphTime(JsonElement ev, string name)
    {
        if (!ev.TryGetProperty(name, out var t) || t.ValueKind != JsonValueKind.Object || Str(t, "dateTime") is not { } s
            || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var zoneId = Str(t, "timeZone") ?? "UTC";
        var zone = zoneId.Equals("UTC", StringComparison.OrdinalIgnoreCase) || !TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var z)
            ? TimeZoneInfo.Utc
            : z;
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }

    private static Person? Email(JsonElement holder) =>
        holder.ValueKind == JsonValueKind.Object && holder.TryGetProperty("emailAddress", out var e) && e.ValueKind == JsonValueKind.Object
            ? new Person(Str(e, "name") ?? Str(e, "address") ?? "Someone", Str(e, "address"))
            : null;

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
