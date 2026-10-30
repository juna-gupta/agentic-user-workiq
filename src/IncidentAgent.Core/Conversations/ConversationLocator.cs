using System.Text.RegularExpressions;

namespace IncidentAgent.Conversations;

public enum ConversationScope
{
    Personal,
    GroupChat,
    Channel,
}

/// <summary>The parts of an incoming Teams activity the bot needs, independent of the Teams SDK types.</summary>
public sealed record TurnInfo
{
    public required string ConversationType { get; init; }

    public required string ConversationId { get; init; }

    public string? ActivityId { get; init; }

    public string? ReplyToId { get; init; }

    /// <summary><c>channelData.team.aadGroupId</c>: the team ID for channel conversations.</summary>
    public string? TeamGroupId { get; init; }

    /// <summary><c>channelData.channel.id</c>, e.g. <c>19:abc@thread.tacv2</c>.</summary>
    public string? ChannelId { get; init; }

    public string? TenantId { get; init; }

    public string? UserAadObjectId { get; init; }

    public string? UserName { get; init; }

    /// <summary>The IANA time zone the user's client reported (<c>activity.localTimezone</c>), when present.</summary>
    public string? TimeZone { get; init; }
}

/// <summary>Where a request points, as a conversation Work IQ can read.</summary>
public abstract record ConversationTarget
{
    private ConversationTarget()
    {
    }

    /// <summary>A 1:1 chat. With an agent user it's a normal Teams chat, so it has a chat ID Work IQ can read.</summary>
    public sealed record Personal(string? ChatId) : ConversationTarget;

    /// <summary>A channel post and its replies.</summary>
    public sealed record ChannelThread(string TeamId, string ChannelId, string RootMessageId, bool CommandIsRoot) : ConversationTarget;

    public sealed record GroupChat(string ChatId) : ConversationTarget;

    public sealed record Unsupported(string Reason) : ConversationTarget;
}

/// <summary>Maps a Teams turn to the Work IQ path that holds its messages.</summary>
public static partial class ConversationLocator
{
    public static ConversationScope ScopeOf(TurnInfo turn) => turn.ConversationType.ToLowerInvariant() switch
    {
        "channel" => ConversationScope.Channel,
        "groupchat" => ConversationScope.GroupChat,
        _ => ConversationScope.Personal,
    };

    public static ConversationTarget Locate(TurnInfo turn)
    {
        switch (ScopeOf(turn))
        {
            case ConversationScope.Personal:
                var personalId = BaseId(turn.ConversationId);
                return new ConversationTarget.Personal(personalId.StartsWith("19:", StringComparison.Ordinal) ? personalId : null);

            case ConversationScope.GroupChat:
                var chatId = BaseId(turn.ConversationId);
                return chatId.Length == 0
                    ? new ConversationTarget.Unsupported("Teams didn't send this chat's ID.")
                    : new ConversationTarget.GroupChat(chatId);

            default:
                if (string.IsNullOrWhiteSpace(turn.TeamGroupId))
                {
                    return new ConversationTarget.Unsupported("Teams didn't say which team this channel belongs to.");
                }

                var channelId = string.IsNullOrWhiteSpace(turn.ChannelId) ? BaseId(turn.ConversationId) : turn.ChannelId!;
                var root = RootMessageId(turn.ConversationId) ?? turn.ReplyToId;
                if (string.IsNullOrWhiteSpace(root))
                {
                    return new ConversationTarget.Unsupported("Send this as a reply in the thread you want me to read.");
                }

                return new ConversationTarget.ChannelThread(turn.TeamGroupId!, channelId, root, root == turn.ActivityId);
        }
    }

    /// <summary>A stable key for a conversation: the chat ID, or the channel ID plus thread root.</summary>
    public static string KeyOf(ConversationTarget target) => target switch
    {
        ConversationTarget.GroupChat g => g.ChatId,
        ConversationTarget.Personal { ChatId: { } id } => id,
        ConversationTarget.ChannelThread t => $"{t.ChannelId};messageid={t.RootMessageId}",
        _ => string.Empty,
    };

    /// <summary>Channel conversation IDs look like <c>19:abc@thread.tacv2;messageid=1712345678901</c>; the message ID is the thread's root post.</summary>
    public static string? RootMessageId(string conversationId)
    {
        var match = MessageIdPart().Match(conversationId);
        return match.Success ? match.Groups["id"].Value : null;
    }

    private static string BaseId(string conversationId)
    {
        var semi = conversationId.IndexOf(';');
        return (semi < 0 ? conversationId : conversationId[..semi]).Trim();
    }

    [GeneratedRegex(@";messageid=(?<id>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex MessageIdPart();
}
