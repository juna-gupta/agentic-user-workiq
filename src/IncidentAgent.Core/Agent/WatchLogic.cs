using IncidentAgent.Conversations;

namespace IncidentAgent.Agent;

/// <summary>The read-along rules, kept apart from the polling loop so they can be tested.</summary>
public static class WatchLogic
{
    /// <summary>Messages from people, posted after <paramref name="since"/>, that weren't sent to the agent and haven't been looked at.</summary>
    public static IReadOnlyList<ChatLine> NewLines(IReadOnlyList<ChatLine> lines, DateTimeOffset since, IReadOnlySet<string> seen) =>
        lines.Where(l => !l.FromSelf && !l.MentionsSelf && l.Created > since && !seen.Contains(l.Id)).ToList();

    /// <summary>Nobody has posted for <paramref name="settle"/>, so the exchange is probably finished.</summary>
    public static bool Settled(IReadOnlyList<ChatLine> newLines, DateTimeOffset now, TimeSpan settle) =>
        newLines.Count > 0 && now - newLines.Max(l => l.Created) >= settle;

    /// <summary>
    /// Whether to read a watched conversation now. When polling, every tick. When Teams delivers all messages, only after
    /// one arrived (<paramref name="heardAt"/>) and nothing else has for <paramref name="settle"/>.
    /// </summary>
    public static bool DueToRead(bool receivesAllMessages, DateTimeOffset? heardAt, DateTimeOffset now, TimeSpan settle) =>
        !receivesAllMessages || (heardAt is { } at && now - at >= settle);
}
