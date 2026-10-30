using System.Globalization;
using System.Text.Json.Nodes;
using IncidentAgent.Agent;
using IncidentAgent.Conversations;
using Microsoft.Extensions.Options;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Clients;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;
using Microsoft.Teams.Core.Schema;

namespace IncidentAgent.Teams;

/// <summary>Where the agent posts: a Teams conversation, and the agent user it posts as.</summary>
public sealed record TeamsDelivery(string ConversationId, Uri ServiceUrl, AgenticIdentity Identity, AgentTurn Turn)
{
    /// <summary>The person the agent is answering, for replies only they should see.</summary>
    public TeamsChannelAccount? Asker { get; init; }

    /// <summary>The message being answered. A reply shown while it's written is attached to it.</summary>
    public string? RequestId { get; init; }
}

/// <summary>
/// Posts as the agent user with the Teams SDK. Every call goes through an API client bound to the agent user's
/// <see cref="AgenticIdentity"/>, so messages, edits and reactions come from the agent user's own account rather
/// than from a bot. After a post in a group chat or channel, the agent reads along there for a while (see
/// <see cref="WatchRegistry"/>).
/// </summary>
public sealed class TeamsSender(WatchRegistry watches, IOptions<TeamsFeatureOptions> features, ILoggerFactory loggers)
{
    private readonly ILogger _logger = loggers.CreateLogger<TeamsSender>();

    private TeamsFeatureOptions Features => features.Value;
    private TeamsBotApplication? _teams;

    public void Attach(TeamsBotApplication teams) => _teams = teams;

    public IAgentOutput For(TeamsDelivery delivery) => new Output(this, delivery);

    /// <summary>Adds or removes the agent user's reaction on a message. Returns false when Teams refused.</summary>
    public async Task<bool> ReactAsync(TeamsDelivery delivery, string messageId, ReactionType reaction, bool add, CancellationToken cancellationToken)
    {
        try
        {
            var conversations = Conversations(delivery);
            if (add)
            {
                await conversations.AddReactionAsync(delivery.ConversationId, messageId, reaction.Value, cancellationToken: cancellationToken);
            }
            else
            {
                await conversations.DeleteReactionAsync(delivery.ConversationId, messageId, reaction.Value, cancellationToken: cancellationToken);
            }

            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Couldn't {Action} the {Reaction} reaction ({Status})", add ? "add" : "remove", reaction.Value, ex.StatusCode);
            return false;
        }
    }

    internal ConversationApiClient Conversations(TeamsDelivery delivery)
    {
        var teams = _teams ?? throw new InvalidOperationException("TeamsSender isn't attached to the Teams app yet.");
        return teams.Api.ForServiceUrl(delivery.ServiceUrl).ForAgenticIdentity(delivery.Identity).Conversations;
    }

    /// <summary>
    /// Turns a reply into a Teams message: markdown, the AI-generated label, and for answers and summaries the
    /// feedback buttons and runbook citations. Drafts become a card, and summaries can carry a status widget.
    /// </summary>
    internal MessageActivityInput Render(AgentReply reply, ConversationScope scope, string? askerId = null)
    {
        var f = features.Value;
        MessageActivityInput input;
        if (reply is { Kind: ReplyKind.Draft, Draft: { } draft } && f.DraftCard)
        {
            input = new MessageActivityInput().AddAdaptiveCardAttachment(System.Text.Json.JsonSerializer.SerializeToElement(DraftCards.Card(draft)));

            // Teams shows the summary in notifications and the chat list; without one they just say "Sent a card".
            input.Properties["summary"] = DraftCards.Summary(draft);
        }
        else if (reply is { Kind: ReplyKind.Summary, Incident: { } incident } && f.HtmlWidget && f.WidgetDomain is { } domain)
        {
            input = IncidentWidget.Message(reply.Markdown, incident, domain);
        }
        else
        {
            input = new MessageActivityInput().WithText(reply.Markdown, TextFormats.Markdown);
        }

        input.AddAIGenerated();
        if (reply.Kind is ReplyKind.Answer or ReplyKind.Summary)
        {
            input.AddFeedback(true);
            foreach (var c in reply.Citations)
            {
                input.AddCitation(c.Position, new CitationAppearance { Name = c.Title, Abstract = c.Abstract, Keywords = [.. c.Keywords] });
            }
        }

        // Teams expects one schema.org Message entity per message, with @type, @context and @id. Set them on the root
        // entity's Properties, and put the label on the same entity as usageInfo, the shape the Teams docs show.
        var root = input.Entities!.OfType<OMessageEntity>().First(e => e.OType == "Message");
        root.Properties["@type"] = "Message";
        root.Properties["@context"] = "https://schema.org";
        root.Properties["@id"] = "";
        if (reply.Kind is ReplyKind.Answer or ReplyKind.Summary or ReplyKind.Draft && !string.IsNullOrWhiteSpace(f.SensitivityLabel))
        {
            var label = new JsonObject { ["@type"] = "CreativeWork", ["name"] = f.SensitivityLabel };
            if (!string.IsNullOrWhiteSpace(f.SensitivityLabelDescription))
            {
                label["description"] = f.SensitivityLabelDescription;
            }

            root.Properties["usageInfo"] = label;
        }

        // Suggested replies go to the person who asked, in 1:1 chats only, so set that person as the recipient.
        if (scope == ConversationScope.Personal && reply.Suggestions.Count > 0)
        {
            var suggestions = new SuggestedActions { Actions = [.. reply.Suggestions.Select(s => new SuggestedAction(ActionTypes.IMBack, s, s))] };
            if (askerId is not null)
            {
                suggestions.AddRecipients(askerId);
            }

            input.WithSuggestedActions(suggestions);
        }

        return input;
    }

    private async Task<string?> PostAsync(TeamsDelivery delivery, AgentReply reply, CancellationToken cancellationToken)
    {
        var scope = delivery.Turn.Scope;
        var input = Render(reply, scope, delivery.Asker?.Id);
        var conversations = Conversations(delivery);

        // Only the person who asked needs to see a failure; everyone else would just see noise.
        if (reply.Private && scope != ConversationScope.Personal && features.Value.TargetedMessages && delivery.Asker is { } asker)
        {
            try
            {
                input.WithRecipient(asker, isTargeted: true);
                var targeted = await conversations.CreateTargetedActivityAsync(delivery.ConversationId, input, cancellationToken: cancellationToken);
                _logger.LogInformation("Posted a {Kind} only the asker sees in {Scope}", reply.Kind, scope);
                return targeted?.Id;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("Teams didn't accept a targeted message ({Status}), so posting it for everyone", ex.StatusCode);
                input.Recipient = null;
            }
        }

        var sent = await conversations.CreateActivityAsync(delivery.ConversationId, input, cancellationToken: cancellationToken);
        _logger.LogInformation("Posted a {Kind} ({Chars} chars) to {Scope}", reply.Kind, reply.Markdown.Length, scope);
        Posted(delivery, sent?.Id);
        return sent?.Id;
    }

    private async Task<bool> UpdateAsync(TeamsDelivery delivery, string messageId, AgentReply reply, CancellationToken cancellationToken)
    {
        try
        {
            await Conversations(delivery).UpdateActivityAsync(delivery.ConversationId, messageId, Render(reply, delivery.Turn.Scope, delivery.Asker?.Id), cancellationToken: cancellationToken);
            _logger.LogInformation("Updated message {MessageId} to a {Kind}", messageId, reply.Kind);
            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Couldn't update message {MessageId} ({Status})", messageId, ex.StatusCode);
            return false;
        }
    }

    private async Task<bool> DeleteAsync(TeamsDelivery delivery, string messageId, CancellationToken cancellationToken)
    {
        try
        {
            await Conversations(delivery).DeleteActivityAsync(delivery.ConversationId, messageId, cancellationToken: cancellationToken);
            _logger.LogInformation("Deleted message {MessageId}", messageId);
            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning("Couldn't delete message {MessageId} ({Status})", messageId, ex.StatusCode);
            return false;
        }
    }

    private void Posted(TeamsDelivery delivery, string? messageId)
    {
        if (delivery.Turn.Scope != ConversationScope.Personal)
        {
            watches.Arm(delivery, PostedAt(messageId));
        }
    }

    /// <summary>Teams message IDs are the creation time in Unix milliseconds, the same clock as a chat message's <c>createdDateTime</c>.</summary>
    private static DateTimeOffset? PostedAt(string? messageId) =>
        long.TryParse(messageId, NumberStyles.None, CultureInfo.InvariantCulture, out var ms) && ms > 1_500_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    /// <summary>One request's output. While a streamed reply is open, the next post completes it.</summary>
    private sealed class Output(TeamsSender sender, TeamsDelivery delivery) : IAgentOutput
    {
        private AgentStream? _stream;

        public async Task<string?> PostAsync(AgentReply reply, CancellationToken cancellationToken)
        {
            if (_stream is { Finished: false } stream)
            {
                var id = await stream.FinishAsync(sender.Render(reply, delivery.Turn.Scope, delivery.Asker?.Id), cancellationToken);
                if (id is not null)
                {
                    sender.Posted(delivery, id);
                    return id;
                }

                if (stream.Cancelled)
                {
                    return null;
                }
            }

            return await sender.PostAsync(delivery, reply, cancellationToken);
        }

        public Task<bool> UpdateAsync(string messageId, AgentReply reply, CancellationToken cancellationToken) =>
            sender.UpdateAsync(delivery, messageId, reply, cancellationToken);

        public Task<bool> DeleteAsync(string messageId, CancellationToken cancellationToken) =>
            sender.DeleteAsync(delivery, messageId, cancellationToken);

        public IReplyStream? OpenStream()
        {
            if (delivery.RequestId is null || !sender.Features.StreamsIn(delivery.Turn.Scope))
            {
                return null;
            }

            return _stream = new AgentStream(sender.Conversations(delivery), delivery.ConversationId, delivery.RequestId, sender._logger);
        }
    }
}
