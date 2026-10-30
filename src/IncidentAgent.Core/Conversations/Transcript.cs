using System.Globalization;
using System.Text;

namespace IncidentAgent.Conversations;

public sealed record ChatLine(string Id, string Author, DateTimeOffset Created, string Text, bool IsRoot = false)
{
    public string? AuthorId { get; init; }

    /// <summary>Posted by the agent user itself.</summary>
    public bool FromSelf { get; init; }

    /// <summary>@mentions the agent user. Teams delivers these to the agent, so the watcher leaves them alone.</summary>
    public bool MentionsSelf { get; init; }
}

/// <summary>The messages a command works on, oldest first, plus a short description of what was read.</summary>
public sealed record Transcript(IReadOnlyList<ChatLine> Lines, string Description, bool Truncated)
{
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>Renders the transcript for a prompt: one <c>[time] Author: text</c> block per message.</summary>
    public string Render(TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Utc;
        var label = zone == TimeZoneInfo.Utc ? "UTC" : zone.Id;
        var sb = new StringBuilder();
        foreach (var line in Lines)
        {
            var at = TimeZoneInfo.ConvertTime(line.Created, zone);
            sb.Append('[').Append(at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append(' ').Append(label).Append("] ");
            sb.Append(line.Author);
            if (line.IsRoot)
            {
                sb.Append(" (started the thread)");
            }

            sb.Append(": ").Append(line.Text.Replace("\n", "\n    ")).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }
}

public sealed class ConversationOptions
{
    public const string Section = "Conversation";

    /// <summary>Group chats: read every message from this many hours back…</summary>
    public int GroupChatHours { get; set; } = 24;

    /// <summary>…or this many recent messages, whichever is more.</summary>
    public int GroupChatMinimumMessages { get; set; } = 25;

    /// <summary>Hard cap on messages read for one command (a busy chat or a long thread).</summary>
    public int MaxMessages { get; set; } = 300;

    /// <summary>Cap on transcript size sent to Work IQ; the oldest messages (never a thread's root) are dropped first.</summary>
    public int MaxTranscriptChars { get; set; } = 40_000;

    /// <summary>Page size for reading messages (50 is the maximum for chat messages and replies).</summary>
    public int PageSize { get; set; } = 50;
}
