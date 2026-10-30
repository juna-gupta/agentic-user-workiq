using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using IncidentAgent.Conversations;
using IncidentAgent.Knowledge;
using IncidentAgent.Shiproom;
using IncidentAgent.WorkIq;

namespace IncidentAgent.Agent;

/// <summary>What the agent says. Kept in one place so the voice stays consistent.</summary>
public static partial class Messages
{
    /// <summary>
    /// Puts each summary section on its own paragraph. The model sometimes runs a label onto the previous line
    /// ("— Mitigated **Impact:** …"), which Teams then renders as one block.
    /// </summary>
    public static string TidySummary(string markdown) => InlineSectionLabel().Replace(markdown.Trim(), "\n\n");

    /// <summary>
    /// Makes sure the summary opens with its "<c>**ID · title (Sev N) — status**</c>" line, built from the model's JSON fields when the
    /// model left it out. Later steps (meeting updates, channel status) rely on it to know which incident the notes are about.
    /// </summary>
    public static string WithHeader(string summary, string? id, string? title, string? severity, string? status)
    {
        var body = summary.TrimStart();
        if ((body.StartsWith("**", StringComparison.Ordinal) && !SectionLabel().IsMatch(body)) || (id is null && title is null))
        {
            return summary;
        }

        var header = new StringBuilder("**");
        header.Append(string.Join(" · ", new[] { id, title }.Where(s => s is not null)));
        if (severity is not null)
        {
            header.Append(" (").Append(severity).Append(')');
        }

        if (status is not null)
        {
            header.Append(" — ").Append(status);
        }

        return header.Append("**\n\n").Append(body).ToString();
    }

    [GeneratedRegex(@"(?<=\S)[ \t]*(?=\*\*(?:Impact|Cause|Fix|Next|Open questions):\*\*)")]
    private static partial Regex InlineSectionLabel();

    [GeneratedRegex(@"^\*\*(?:Impact|Cause|Fix|Next|Open questions):\*\*")]
    private static partial Regex SectionLabel();

    public static string Welcome(string name, ConversationScope scope) =>
        $"Hi {(scope == ConversationScope.Channel ? "team" : "all")}, I'm {name}, the team's incident agent. I can catch you up on an incident, answer runbook questions, " +
        "and prepare updates for meetings like shiproom. @mention me when you need something. For a few minutes after I post, I keep reading along, " +
        "so I can pick up a follow-up without an @mention.";

    public const string NothingToSummarize = "There's nothing here for me to summarize yet.";

    public const string NoDraft = "I don't have an update waiting to go in a deck. Ask me to prepare one first.";

    public const string DraftFailed = "I couldn't put that update together this time. Ask me again in a moment.";

    public const string Busy = "I've got a lot going on right now. Give me a minute and ask again.";

    public const string TimedOut = "That took longer than it should, so I stopped. Try again in a minute.";

    public const string SomethingWentWrong = "Something went wrong while I was working on that. Ask me again in a moment.";

    public const string CheckingRunbook = "Checking the runbook";

    public static string Reading(ConversationScope scope) => scope switch
    {
        ConversationScope.Personal => "Reading our chat",
        ConversationScope.Channel => "Reading this thread",
        _ => "Reading this chat",
    };

    public static string Summarizing(Transcript transcript) =>
        transcript.IsEmpty ? "Pulling the incident together from my notes" : $"Pulling the incident together from {transcript.Description}";

    public static string FindingMeeting(string keyword) => $"Looking for the {keyword} meeting on my calendar";

    public static string ReadingDeck(string deckName) => $"Copying the slide format from {deckName}";

    public static string DraftingUpdate(Meeting meeting) => $"Drafting the update for {meeting.Subject}";

    public static string ReadingCases(IEnumerable<string> ids) => $"Reading {string.Join(", ", ids)} in the runbook";

    public static string SuggestSummary(string? incidentId) => incidentId is null ? "Summarize the incident" : $"Summarize {incidentId}";

    public const string SuggestShiproom = "Prepare the shiproom update";

    public const string DraftEmpty = "The update needs at least one line.";

    public static string WorkIqFailed(WorkIqException ex) => ex switch
    {
        RunbookNotFoundException missing => $"I couldn't find my runbook at {missing.Path} in my OneDrive. Ask my owner to upload it with scripts/seed_onedrive.py.",
        _ => ex.Failure switch
        {
            WorkIqFailure.Unauthorized => "I can't reach Microsoft 365 right now: Work IQ didn't accept my sign-in. An admin needs to check my Work IQ access.",
            WorkIqFailure.Forbidden => "Work IQ says I'm not allowed to open that. I may need to be added to it, or my Work IQ access needs a look.",
            WorkIqFailure.NotFound => "I couldn't find that in Microsoft 365. It may have moved, or it isn't shared with me.",
            WorkIqFailure.Throttled => "Work IQ is busy right now. Give it a minute and ask again.",
            _ => "I couldn't reach Work IQ just now. Try again in a minute.",
        },
    };

    /// <summary>Added to a reply written without the runbook because it isn't in the agent user's OneDrive yet.</summary>
    public static string RunbookMissing(string path) =>
        $"I couldn't find my runbook at {path} in my OneDrive, so this reply doesn't use it. Ask my owner to upload it with scripts/seed_onedrive.py.";

    public static string MeetingNotFound(string keyword, DateOnly? day) =>
        $"I couldn't find a {keyword} meeting {(day is { } d ? "on " + d.ToString("dddd, MMM d", CultureInfo.InvariantCulture) : "in the next week")} on my calendar. " +
        "If I should be preparing for it, add me to the invite.";

    public static string Working(Meeting meeting, string? incidentId, TimeZoneInfo zone) =>
        $"On it. I'll draft the {incidentId ?? "incident"} update for **{meeting.Subject}** ({When(meeting, zone)}).";

    /// <summary>When the meeting is and who's in it, leaving out the agent itself.</summary>
    public static string DraftSubtitle(Meeting meeting, TimeZoneInfo zone, string selfName)
    {
        var sb = new StringBuilder(When(meeting, zone));
        var others = meeting.Attendees
            .Where(a => !IsSelf(a, selfName) && !string.Equals(a.Email, meeting.Organizer?.Email, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name)
            .ToList();
        if (meeting.Organizer is { } organizer)
        {
            sb.Append(" · organizer ").Append(organizer.Name);
        }

        if (others.Count > 0)
        {
            sb.Append(" · with ").Append(string.Join(", ", others));
        }

        return sb.ToString();
    }

    public static string DraftFootnote(string? priorDeck, int? templateSlide, IReadOnlyList<string> changes)
    {
        var sb = new StringBuilder(priorDeck is null
            ? "There was no earlier deck, so I used a standard format."
            : $"Format from {priorDeck}{(templateSlide is { } n ? $", slide {n}" : string.Empty)}.");
        if (changes.Count > 0)
        {
            sb.Append(" Updated since my summary: ").Append(string.Join("; ", changes)).Append('.');
        }

        return sb.ToString();
    }

    /// <summary>The draft as a chat message, with a line saying where it stands.</summary>
    public static string Draft(ShiproomDraft draft)
    {
        var sb = new StringBuilder();
        sb.Append("**").Append(draft.Title).Append("**, ").Append(draft.Subtitle);
        sb.Append("\n\n**").Append(draft.Lines[0].Text).Append("**\n");
        foreach (var line in draft.Lines.Skip(1))
        {
            sb.Append("- ").Append(BoldLabel(line.Text)).Append('\n');
        }

        sb.Append('\n').Append(draft.Footnote).Append('\n').Append(DraftState(draft));
        return sb.ToString();
    }

    /// <summary>Where the draft stands, in one line under it.</summary>
    public static string DraftState(ShiproomDraft draft)
    {
        var deck = draft.Deck is { } d ? Link(d) : "the deck";
        var by = draft.ActedBy is { } who ? $" by {who}" : string.Empty;
        return draft.Status switch
        {
            DraftStatus.Posted => $"Approved{by}. The text is in my next message, ready to paste into {deck}.",
            DraftStatus.Discarded => $"Discarded{by}.",
            _ when draft.Deck is null => $"{Edited(draft)}The invite doesn't link this week's deck. Approve it and I'll post the text ready to paste.",
            _ => $"{Edited(draft)}Approve it and I'll post the text ready to paste into {deck}.",
        };
    }

    public const string DraftOutOfDate = "That draft is out of date or already handled. Ask me for a new one if you need it.";

    public const string DraftBusy = "I'm still working on that draft. Try again in a moment.";

    public static string DraftDiscarded(string? who) => who is null ? "Discarded the draft." : $"{who} discarded the draft.";

    /// <summary>An approved draft as slide text, with a note naming the deck it goes in.</summary>
    public static string ReadyToPaste(ShiproomDraft draft)
    {
        var deck = draft.Deck is { } d ? Link(d) : "this week's deck";
        var sb = new StringBuilder($"Here's the {draft.SlideTitle} slide for {deck}, ready to paste:\n\n");
        foreach (var line in draft.Lines)
        {
            sb.Append(line.Bold ? $"**{line.Text}**" : line.Text).Append("  \n");
        }

        return sb.ToString().TrimEnd();
    }

    private static string Edited(ShiproomDraft draft) => draft.ActedBy is { } editor ? $"Edited by {editor}. " : string.Empty;

    public static string Describe(Meeting meeting, TimeZoneInfo zone)
    {
        var sb = new StringBuilder();
        sb.Append(meeting.Subject).Append(", ").Append(When(meeting, zone)).Append('–')
          .Append(TimeZoneInfo.ConvertTime(meeting.End, zone).ToString("h:mm tt", CultureInfo.InvariantCulture)).Append(" (").Append(zone.Id).Append(").");
        if (meeting.Organizer is { } o)
        {
            sb.Append(" Organizer: ").Append(o.Name).Append('.');
        }

        if (meeting.Attendees.Count > 0)
        {
            sb.Append(" Invited: ").Append(string.Join(", ", meeting.Attendees.Select(a => a.Name))).Append('.');
        }

        var files = meeting.Links.Count(DeckFinder.IsFileLink);
        if (files > 0)
        {
            sb.Append(" The invite links ").Append(files).Append(files == 1 ? " file or folder." : " files or folders.");
        }

        return sb.ToString();
    }

    public static string When(Meeting meeting, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(meeting.Start, zone).ToString("ddd MMM d, h:mm tt", CultureInfo.InvariantCulture);

    private static string Link(DeckFile deck) => deck.WebUrl is { } url ? $"[{deck.Name}]({url})" : deck.Name;

    /// <summary>The agent user's display name is usually its name plus a role, like "Iris (Incident Agent)".</summary>
    private static bool IsSelf(Person person, string selfName) =>
        person.Name.Equals(selfName, StringComparison.OrdinalIgnoreCase)
        || person.Name.StartsWith(selfName + " ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Turns "Impact: Users saw..." into "**Impact:** Users saw...".</summary>
    public static string BoldLabel(string line)
    {
        var colon = line.IndexOf(':');
        return colon is > 0 and <= 20 ? $"**{line[..(colon + 1)]}**{line[(colon + 1)..]}" : line;
    }
}
