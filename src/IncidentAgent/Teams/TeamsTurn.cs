using IncidentAgent.Conversations;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Schema;

namespace IncidentAgent.Teams;

public static class TeamsTurn
{
    /// <summary>Copies what the Teams-agnostic core needs from a Teams activity.</summary>
    public static TurnInfo From(TeamsActivity activity) => new()
    {
        ConversationType = activity.Conversation?.ConversationType?.Value ?? "personal",
        ConversationId = activity.Conversation?.Id ?? string.Empty,
        ActivityId = activity.Id,
        ReplyToId = RepliedToId(activity),
        TeamGroupId = activity.ChannelData?.Team?.AadGroupId,
        ChannelId = activity.ChannelData?.Channel?.Id,
        TenantId = activity.TenantId ?? activity.Conversation?.TenantId,
        UserAadObjectId = activity.From?.AadObjectId,
        UserName = activity.From?.Name,
        // Read the time zone from localTimezone, falling back to the clientInfo entity.
        TimeZone = activity.LocalTimezone ?? activity.Entities?.OfType<ClientInfoEntity>().FirstOrDefault()?.Timezone,
    };

    /// <summary>
    /// The message an activity replies or reacts to. Typed handler activities read it from the copy
    /// <see cref="KeepReplyToId"/> keeps in the activity's extra properties.
    /// </summary>
    public static string? RepliedToId(TeamsActivity activity) =>
        activity.ReplyToId
        ?? activity.Properties.GetValueOrDefault(KeepReplyToId.Key) as string;
}

/// <summary>
/// Runs before the incoming activity becomes a typed one (a reaction, an invoke) and keeps replyToId in the
/// activity's extra properties, so typed handlers can read it.
/// </summary>
public sealed class KeepReplyToId : ITurnMiddleware
{
    public const string Key = "iris.replyToId";

    public Task OnTurnAsync(BotApplication botApplication, CoreActivity activity, NextTurn nextTurn, CancellationToken cancellationToken = default)
    {
        if (activity.ReplyToId is { } id)
        {
            activity.Properties[Key] = id;
        }

        return nextTurn(cancellationToken);
    }
}
