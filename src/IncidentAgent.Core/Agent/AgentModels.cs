using System.Text.Json;
using IncidentAgent.Conversations;
using IncidentAgent.Shiproom;
using IncidentAgent.WorkIq;

namespace IncidentAgent.Agent;

public sealed class AgentOptions
{
    public const string Section = "Agent";

    public const string DefaultRole = "the helpdesk engineering team's incident agent";

    /// <summary>What the agent calls itself in prompts and replies (the agent user's first name).</summary>
    public string Name { get; set; } = "Iris";

    /// <summary>Who the agent is to the team, as in "You are {Name}, {Role}." in every prompt.</summary>
    public string Role { get; set; } = DefaultRole;

    /// <summary>Used for "today" and "tomorrow" when Teams doesn't send the user's time zone (the watcher never has one).</summary>
    public string DefaultTimeZone { get; set; } = "America/Los_Angeles";

    /// <summary>Say hello when someone adds the agent to a group chat or team.</summary>
    public bool GreetWhenAdded { get; set; } = true;

    public TimeZoneInfo ZoneFor(string? clientZone)
    {
        foreach (var id in new[] { clientZone, DefaultTimeZone })
        {
            if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone))
            {
                return zone;
            }
        }

        return TimeZoneInfo.Utc;
    }
}

/// <summary>
/// By default Teams delivers only the group and channel messages that @mention the agent. To pick up work nobody asked
/// it for, the agent reads a conversation for a while after it last posted there: by polling (one Work IQ call per
/// poll), or, when the blueprint delivers all messages, after each delivered message.
/// </summary>
public sealed class WatchOptions
{
    public const string Section = "Watch";

    public bool Enabled { get; set; } = true;

    public int PollSeconds { get; set; } = 10;

    /// <summary>How long after its last post the agent keeps reading a conversation.</summary>
    public int WindowMinutes { get; set; } = 10;

    /// <summary>Wait until nobody has posted for this long, so a back-and-forth is read as one exchange.</summary>
    public int SettleSeconds { get; set; } = 5;

    /// <summary>The agent acts on its own only when the model is at least this sure the team wants it to.</summary>
    public double MinConfidence { get; set; } = 0.7;

    public int MessagesPerPoll { get; set; } = 10;

    /// <summary>
    /// Set when the agent blueprint's communicationConfiguration delivers every group chat and channel message
    /// (messageNotificationMode <c>allMessages</c>), not just @mentions. The agent then reads a conversation only after
    /// Teams delivers a message there, instead of polling it every <see cref="PollSeconds"/>.
    /// </summary>
    public bool ReceivesAllMessages { get; set; }
}

public enum AgentIntentKind
{
    None,
    Answer,
    Summarize,
    ShiproomUpdate,
    AddToDeck,
}

public sealed record AgentIntent(AgentIntentKind Kind, double Confidence, string? When, string? Meeting, string? Incident, string Reason)
{
    public static readonly AgentIntent Unclear = new(AgentIntentKind.None, 0, null, null, null, "unclear");

    public static AgentIntent Parse(string? text)
    {
        if (!WorkIqEnvelope.TryParseObject(text, out var json))
        {
            return Unclear;
        }

        var kind = Str(json, "intent")?.ToLowerInvariant() switch
        {
            "answer" => AgentIntentKind.Answer,
            "summarize" or "summary" => AgentIntentKind.Summarize,
            "shiproom_update" or "meeting_update" => AgentIntentKind.ShiproomUpdate,
            "add_to_deck" => AgentIntentKind.AddToDeck,
            _ => AgentIntentKind.None,
        };
        var confidence = json.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var d) ? Math.Clamp(d, 0, 1) : 0;
        return new AgentIntent(kind, confidence, Str(json, "when"), Str(json, "meeting"), IncidentId(Str(json, "incident")), Str(json, "reason") ?? string.Empty);
    }

    /// <summary>Normalizes "inc-2304" to "INC-2304"; anything that isn't an incident ID is dropped.</summary>
    public static string? IncidentId(string? id) =>
        id is not null && System.Text.RegularExpressions.Regex.Match(id, @"\b[A-Za-z]{2,6}-\d{2,7}\b") is { Success: true } m ? m.Value.ToUpperInvariant() : null;

    private static string? Str(JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s && s != "null" ? s : null;
}

/// <summary>An incident summary the agent wrote; it reuses it for meeting updates and status posts elsewhere.</summary>
public sealed record IncidentNote(string? Id, string Title, string Summary, DateTimeOffset At)
{
    /// <summary>Facts learned after <see cref="Summary"/> was written, oldest first. Where they conflict with it, they win.</summary>
    public IReadOnlyList<string> Updates { get; init; } = [];

    /// <summary>The same notes plus facts learned later, so the next summary (in any conversation) starts from the newest facts.</summary>
    public IncidentNote WithUpdates(IReadOnlyList<string> changes, DateTimeOffset at) =>
        changes.Count == 0 ? this : this with { Updates = [.. Updates, .. changes], At = at };
}

public enum DraftStatus
{
    /// <summary>Posted and waiting for someone to approve it, edit it or discard it.</summary>
    Pending,

    /// <summary>Approved. The agent posted the text ready to paste into the deck.</summary>
    Posted,
    Discarded,
}

/// <summary>A meeting update the agent posted and is waiting for the team to confirm.</summary>
public sealed record ShiproomDraft(Meeting Meeting, DeckFile? Deck, string SlideTitle, IReadOnlyList<DeckLine> Lines, string? IncidentId, DateTimeOffset At)
{
    public string Title { get; init; } = $"Update for {Meeting.Subject}";

    /// <summary>When the meeting is and who's in it.</summary>
    public string Subtitle { get; init; } = string.Empty;

    /// <summary>Where the format came from and what changed since the agent's summary.</summary>
    public string Footnote { get; init; } = string.Empty;

    /// <summary>The message the draft was posted in, so it can be updated in place as the team acts on it.</summary>
    public string? MessageId { get; init; }

    public DraftStatus Status { get; init; }

    /// <summary>Who last acted on the draft (edited, approved or discarded it).</summary>
    public string? ActedBy { get; init; }

    /// <summary>Identifies this version of the draft on a card, so an action on an older card is recognized as stale.</summary>
    public string Stamp => At.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public bool CanAct => Status is DraftStatus.Pending;

    public bool Matches(DraftRef reference) =>
        (reference.Stamp is null || reference.Stamp == Stamp)
        && (reference.MessageId is null || MessageId is null || reference.MessageId == MessageId);
}

/// <summary>What a card action or reaction points at: the draft's stamp from the card, or the message it's on.</summary>
public sealed record DraftRef(string? Stamp, string? MessageId)
{
    public static readonly DraftRef Current = new(null, null);
}

/// <summary>The draft after someone edited or discarded it, or why that didn't happen.</summary>
public sealed record DraftChange(ShiproomDraft? Draft, string? Problem)
{
    public static DraftChange Failed(string problem) => new(null, problem);
}

/// <summary>Where the agent is working: a conversation and the agent user's own ID in it.</summary>
public sealed record AgentTurn(ConversationTarget Target, ConversationScope Scope, string SelfId, TimeZoneInfo Zone)
{
    public string Key => ConversationLocator.KeyOf(Target);
}

public enum ReplyKind
{
    /// <summary>"On it" and similar progress notes.</summary>
    Status,
    Answer,
    Summary,
    Draft,

    /// <summary>An approved draft as slide text, ready to paste into the deck.</summary>
    PasteReady,
    Notice,
    Error,
}

/// <summary>A runbook case cited in a reply, at the <c>[n]</c> marker with the same <see cref="Position"/>.</summary>
public sealed record Citation(int Position, string Title, string Abstract, IReadOnlyList<string> Keywords);

/// <summary>
/// Something the agent says. The markdown always stands on its own; the rest lets a channel present it better, like
/// Teams does with citations, suggested replies and cards.
/// </summary>
public sealed record AgentReply(string Markdown, ReplyKind Kind)
{
    public IReadOnlyList<Citation> Citations { get; init; } = [];

    /// <summary>Short follow-ups the person could send next.</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    public ShiproomDraft? Draft { get; init; }

    /// <summary>The incident a summary is about.</summary>
    public IncidentNote? Incident { get; init; }

    /// <summary>Only the person who asked needs to see it (a failure, say), where the channel can do that.</summary>
    public bool Private { get; init; }

    public static AgentReply Error(string markdown) => new(markdown, ReplyKind.Error) { Private = true };

    public static AgentReply Notice(string markdown) => new(markdown, ReplyKind.Notice);
}

/// <summary>Posts to the conversation the agent is working in.</summary>
public interface IAgentOutput
{
    /// <summary>Posts a reply and returns its message ID when the channel has one.</summary>
    Task<string?> PostAsync(AgentReply reply, CancellationToken cancellationToken);

    /// <summary>Replaces a message the agent posted. Returns false when it couldn't (the message is gone, say).</summary>
    Task<bool> UpdateAsync(string messageId, AgentReply reply, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string messageId, CancellationToken cancellationToken);

    /// <summary>
    /// Starts showing the reply to the current request while it's written, or returns null where that isn't supported.
    /// The next <see cref="PostAsync"/> ends the stream with that reply.
    /// </summary>
    IReplyStream? OpenStream();
}

/// <summary>A reply shown while it's being written.</summary>
public interface IReplyStream
{
    /// <summary>The person stopped the reply, so the rest of it won't be shown.</summary>
    bool Cancelled { get; }

    /// <summary>Says what the agent is doing. Ignored once the reply text has started.</summary>
    Task InformAsync(string text, CancellationToken cancellationToken);

    Task AppendAsync(string text, CancellationToken cancellationToken);
}

public static class AgentOutputExtensions
{
    public static Task<string?> PostAsync(this IAgentOutput output, string markdown, CancellationToken cancellationToken) =>
        output.PostAsync(AgentReply.Notice(markdown), cancellationToken);
}
