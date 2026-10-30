using IncidentAgent.Agent;
using IncidentAgent.Shiproom;
using IncidentAgent.WorkIq;

namespace IncidentAgent.Tests;

public class ShiproomTests
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    private const string Event = """
    {
      "id": "ev1", "subject": "Helpdesk Shiproom", "isCancelled": false,
      "start": {"dateTime": "2026-10-11T17:00:00.0000000", "timeZone": "UTC"},
      "end": {"dateTime": "2026-10-11T17:30:00.0000000", "timeZone": "UTC"},
      "organizer": {"emailAddress": {"name": "Daniel Ortiz", "address": "daniel@contoso.com"}},
      "attendees": [
        {"type": "required", "emailAddress": {"name": "Maya Chen", "address": "maya@contoso.com"}},
        {"type": "required", "emailAddress": {"name": "Iris (Incident Agent)", "address": "iris@contoso.com"}}
      ],
      "body": {"contentType": "html", "content": "<p>Deck: <a href=\"https://contoso.sharepoint.com/:p:/r/sites/hd/Shiproom/deck.pptx?d=1&amp;csf=1\">deck</a> <a href=\"https://teams.microsoft.com/l/meetup-join/x\">Join</a></p>"},
      "webLink": "https://outlook.office365.com/owa/?itemid=ev1"
    }
    """;

    [Fact]
    public void Draft_drops_a_copied_slide_heading()
    {
        var (lines, changes) = IncidentAgentService.ParseDraft(
            """{"lines":["Slide 3: Incident updates","INC-2304 · Staging 401s (Sev 3) — Mitigated","Impact: Pilot only."],"changes":["Sev 2 to Sev 3"]}""");

        Assert.Equal(["INC-2304 · Staging 401s (Sev 3) — Mitigated", "Impact: Pilot only."], lines);
        Assert.Equal(["Sev 2 to Sev 3"], changes);
    }

    [Fact]
    public void Reads_a_meeting_from_the_calendar()
    {
        var meeting = CalendarReader.Parse(FakeWorkIq.Json(Event))!;

        Assert.Equal("Helpdesk Shiproom", meeting.Subject);
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 17, 0, 0, TimeSpan.Zero), meeting.Start);
        Assert.Equal("Daniel Ortiz", meeting.Organizer?.Name);
        Assert.Equal(["Maya Chen", "Iris (Incident Agent)"], meeting.Attendees.Select(a => a.Name));
        Assert.Equal("https://contoso.sharepoint.com/:p:/r/sites/hd/Shiproom/deck.pptx?d=1&csf=1", meeting.Links[0]);
        Assert.Equal([true, false], meeting.Links.Select(DeckFinder.IsFileLink));
    }

    [Fact]
    public void Skips_cancelled_meetings()
    {
        Assert.Null(CalendarReader.Parse(FakeWorkIq.Json(Event.Replace("\"isCancelled\": false", "\"isCancelled\": true"))));
    }

    [Theory]
    [InlineData("Helpdesk Shiproom", "shiproom", true)]
    [InlineData("Helpdesk Shiproom", "helpdesk shiproom", true)]
    [InlineData("Helpdesk Standup", "shiproom", false)]
    public void Matches_meetings_by_keyword(string subject, string keyword, bool expected)
    {
        Assert.Equal(expected, CalendarReader.Matches(subject, keyword));
    }

    [Fact]
    public void Day_boundaries_use_the_team_time_zone()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 11, 7, 0, 0, TimeSpan.Zero), CalendarReader.StartOfDay(new DateOnly(2026, 10, 11), Pacific).ToUniversalTime());
    }

    [Theory]
    [InlineData("Helpdesk Shiproom - 2026-10-04.pptx", "2026-10-04")]
    [InlineData("Shiproom notes.pptx", null)]
    public void Finds_the_date_in_a_deck_name(string name, string? expected)
    {
        Assert.Equal(expected is null ? null : DateOnly.Parse(expected), DeckFinder.DateIn(name));
    }

    [Fact]
    public void Reads_the_template_slide_from_last_weeks_deck()
    {
        var deck = DeckReader.Read(Fixtures.Read("prior-deck.pptx"), "prior.pptx");

        Assert.Equal(["Helpdesk Shiproom", "Release train", "Incident updates", "Risks & asks"], deck.Slides.Select(s => s.Title));
        var incidents = deck.FindSlide("incident updates")!;
        Assert.Equal(3, incidents.Number);
        Assert.StartsWith("INC-2291 · Ticket sync delays (Sev 3) — Mitigated", incidents.Lines[0].Text);
        Assert.True(incidents.Lines[0].Bold);
        Assert.Equal(1, incidents.Lines[1].Level);
        Assert.Contains("Incident format", incidents.Notes);
        Assert.Contains(deck.Slides[1].Lines, l => l.Text.StartsWith("Helpdesk bot 2.3 | Staging |", StringComparison.Ordinal));
    }

    private static readonly DeckFile NextDeck = new("b!drive", "item1", "Helpdesk Shiproom - 2026-10-11.pptx", "https://contoso.sharepoint.com/deck", null);

    [Fact]
    public void The_draft_names_the_organizer_and_other_attendees_but_not_the_agent()
    {
        var meeting = CalendarReader.Parse(FakeWorkIq.Json(Event))!;
        var draft = new ShiproomDraft(meeting, NextDeck, "Incident updates", [new("INC-2304 · Staging (Sev 3) — Mitigated", 0, true), new("Impact: 40 chats.", 1)], "INC-2304", DateTimeOffset.UtcNow)
        {
            Subtitle = Messages.DraftSubtitle(meeting, Pacific, "Iris"),
            Footnote = Messages.DraftFootnote("prior.pptx", 3, ["Sev 2 to Sev 3"]),
        };

        var text = Messages.Draft(draft);

        Assert.Equal("Sun Oct 11, 10:00 AM · organizer Daniel Ortiz · with Maya Chen", draft.Subtitle);
        Assert.Contains("**INC-2304 · Staging (Sev 3) — Mitigated**\n", text);
        Assert.Contains("- **Impact:** 40 chats.", text);
        Assert.Contains("Format from prior.pptx, slide 3. Updated since my summary: Sev 2 to Sev 3.", text);
        Assert.Contains("[Helpdesk Shiproom - 2026-10-11.pptx](https://contoso.sharepoint.com/deck)", text);
    }

    [Fact]
    public void An_approved_draft_is_posted_ready_to_paste_and_names_the_deck()
    {
        var meeting = CalendarReader.Parse(FakeWorkIq.Json(Event))!;
        var draft = new ShiproomDraft(meeting, NextDeck, "Incident updates", [new("INC-2304 Staging bot 401s (Sev 3) Mitigated", 0, true), new("Impact: 40 chats.", 1)], "INC-2304", DateTimeOffset.UtcNow);

        var text = Messages.ReadyToPaste(draft);

        Assert.StartsWith("Here's the Incident updates slide for [Helpdesk Shiproom - 2026-10-11.pptx](https://contoso.sharepoint.com/deck), ready to paste:", text);
        Assert.EndsWith("ready to paste:\n\n**INC-2304 Staging bot 401s (Sev 3) Mitigated**  \nImpact: 40 chats.", text);
    }

    [Fact]
    public void Without_a_deck_the_paste_ready_text_says_this_weeks_deck()
    {
        var meeting = CalendarReader.Parse(FakeWorkIq.Json(Event))!;
        var draft = new ShiproomDraft(meeting, null, "Incident updates", [new("Impact: 40 chats.", 1)], "INC-2304", DateTimeOffset.UtcNow);

        Assert.StartsWith("Here's the Incident updates slide for this week's deck, ready to paste:", Messages.ReadyToPaste(draft));
    }
}