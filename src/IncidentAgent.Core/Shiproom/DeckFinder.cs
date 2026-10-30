using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.Logging;

namespace IncidentAgent.Shiproom;

public sealed record DeckFile(string DriveId, string ItemId, string Name, string? WebUrl, DateTimeOffset? Modified)
{
    /// <summary>The date in the file name (<c>Helpdesk Shiproom - 2026-10-11.pptx</c>), if there is one.</summary>
    public DateOnly? Date => DeckFinder.DateIn(Name);
}

/// <param name="Current">The deck for this meeting, where the update goes.</param>
/// <param name="Prior">The most recent earlier deck, whose incident slide is the template.</param>
public sealed record ShiproomDecks(DeckFile? Current, DeckFile? Prior);

/// <summary>
/// Finds the meeting's decks the way a person would: follow the links in the invite. A linked deck is this meeting's
/// deck; a linked folder holds the earlier decks.
/// </summary>
public sealed partial class DeckFinder(ILogger<DeckFinder> logger)
{
    private const string Pptx = ".pptx";

    public async Task<ShiproomDecks> FindAsync(IWorkIq workIq, Meeting meeting, TimeZoneInfo zone, CancellationToken cancellationToken)
    {
        var links = meeting.Links.Where(IsFileLink).Take(5).ToList();
        if (links.Count == 0)
        {
            logger.LogInformation("The invite for '{Subject}' has no file links", meeting.Subject);
            return new ShiproomDecks(null, null);
        }

        var linked = new List<DeckFile>();
        var folders = new List<(string DriveId, string ItemId)>();
        foreach (var result in await workIq.FetchAsync(links.Select(WorkIqPaths.SharedItem).ToList(), cancellationToken))
        {
            if (!result.IsSuccess)
            {
                logger.LogInformation("Couldn't open an invite link ({Status}): {Error}", result.StatusCode, result.Error);
                continue;
            }

            var item = result.Data!.Value;
            if (item.TryGetProperty("folder", out _) && DriveIds(item) is { } folder)
            {
                folders.Add(folder);
            }
            else if (ToDeck(item) is { } deck)
            {
                linked.Add(deck);
            }
        }

        var all = new Dictionary<string, DeckFile>(StringComparer.Ordinal);
        foreach (var deck in linked)
        {
            all.TryAdd(deck.ItemId, deck);
        }

        if (folders.Count > 0)
        {
            var listings = await workIq.FetchAsync(folders.Select(f => WorkIqPaths.DriveChildren(f.DriveId, f.ItemId, 100)).ToList(), cancellationToken);
            foreach (var listing in listings.Where(l => l.IsSuccess))
            {
                foreach (var child in listing.Items)
                {
                    if (ToDeck(child) is { } deck)
                    {
                        all.TryAdd(deck.ItemId, deck);
                    }
                }
            }
        }

        var meetingDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(meeting.Start, zone).DateTime);
        var current = linked.FirstOrDefault(d => d.Date == meetingDay)
            ?? all.Values.FirstOrDefault(d => d.Date == meetingDay)
            ?? linked.FirstOrDefault();

        var prior = all.Values
            .Where(d => d.ItemId != current?.ItemId)
            .Select(d => (Deck: d, Day: d.Date ?? (d.Modified is { } m ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(m, zone).DateTime) : DateOnly.MinValue)))
            .Where(x => x.Day < meetingDay)
            .OrderByDescending(x => x.Day)
            .Select(x => x.Deck)
            .FirstOrDefault();

        logger.LogInformation("Decks for '{Subject}': {Count} found; current {Current}, prior {Prior}",
            meeting.Subject, all.Count, current?.Name ?? "none", prior?.Name ?? "none");
        return new ShiproomDecks(current, prior);
    }

    public static DateOnly? DateIn(string name)
    {
        var m = IsoDate().Match(name);
        return m.Success && DateOnly.TryParseExact(m.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }

    /// <summary>OneDrive and SharePoint links only; Teams join links and other URLs aren't files.</summary>
    public static bool IsFileLink(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("1drv.ms", StringComparison.OrdinalIgnoreCase));

    private static DeckFile? ToDeck(JsonElement item)
    {
        if (!item.TryGetProperty("file", out _) || Str(item, "name") is not { } name || !name.EndsWith(Pptx, StringComparison.OrdinalIgnoreCase)
            || DriveIds(item) is not { } ids)
        {
            return null;
        }

        DateTimeOffset? modified = Str(item, "lastModifiedDateTime") is { } s
            && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m) ? m : null;
        return new DeckFile(ids.DriveId, ids.ItemId, name, Str(item, "webUrl"), modified);
    }

    private static (string DriveId, string ItemId)? DriveIds(JsonElement item) =>
        Str(item, "id") is { } id && item.TryGetProperty("parentReference", out var parent) && Str(parent, "driveId") is { } drive
            ? (drive, id)
            : null;

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}")]
    private static partial Regex IsoDate();
}
