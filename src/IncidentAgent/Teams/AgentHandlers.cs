#pragma warning disable ExperimentalTeamsHtmlWidget

using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentAgent.Agent;
using IncidentAgent.Conversations;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.Options;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.HtmlWidget;
using Microsoft.Teams.Apps.Schema;
using Microsoft.Teams.Apps.Schema.Entities;
using Microsoft.Teams.Apps.TaskModules;
using Microsoft.Teams.Core.Schema;

namespace IncidentAgent.Teams;

/// <summary>
/// The agent's Teams routes. By default messages reach it like they reach any member: everything in its 1:1 chats, and
/// @mentions in group chats and channels. When the blueprint delivers all messages, the ones that don't @mention the agent
/// only feed the read-along (<see cref="WatchRegistry"/>). A message delivered twice is handled once. Each request is
/// acknowledged with a typing indicator (and in group chats and channels, an eyes reaction that turns into a check
/// mark when the agent is done) and handled on the <see cref="WorkQueue"/>.
/// </summary>
/// <remarks>
/// The other routes cover what people do with the agent's messages: the draft card's buttons and Edit dialog, a like or
/// check mark on the draft to approve it, the feedback buttons, and the status widget's Refresh button.
/// </remarks>
public sealed class AgentHandlers(
    IncidentAgentService agent,
    TeamsSender sender,
    WatchRegistry watches,
    SeenActivities seen,
    TimeProvider clock,
    IWorkIqFactory workIqFactory,
    AgentTokens tokens,
    WorkQueue queue,
    IOptions<AgentOptions> options,
    IOptions<TeamsFeatureOptions> features,
    ILogger<AgentHandlers> logger)
{
    /// <summary>Teams traffic. Agent users also get activities for other channels, such as email notifications ("agents").</summary>
    private const string TeamsChannel = "msteams";

    /// <summary>Reaction IDs in the form Teams uses for its own emoji (code point, then name).</summary>
    private static readonly ReactionType Working = new("1f440_eyes");
    private static readonly ReactionType Done = new("2705_whiteheavycheckmark");

    /// <summary>Lets Teams show the card's response before the draft's message is removed.</summary>
    private static readonly TimeSpan DeleteDelay = TimeSpan.FromSeconds(1);

    public void Map(TeamsBotApplication teams)
    {
        sender.Attach(teams);
        teams.OnMessage(OnMessageAsync);
        teams.OnConversationUpdate(OnConversationUpdateAsync);
        teams.OnMessageReaction(OnReactionAsync);
        teams.OnAdaptiveCardAction(OnCardActionAsync);
        teams.OnTaskFetch(OnTaskFetchAsync);
        teams.OnTaskSubmit(OnTaskSubmitAsync);
        teams.OnMessageSubmitFeedback(OnFeedbackAsync);
        teams.OnWidgetCallTool(OnWidgetToolAsync);
        teams.OnAgentLifecycle(OnLifecycleAsync);
        teams.OnEvent(OnEventAsync);
    }

    private async Task OnMessageAsync(Context<MessageActivity> context, CancellationToken cancellationToken)
    {
        var activity = context.Activity;
        if (!seen.FirstTime(activity.Conversation?.Id, activity.Id, clock.GetUtcNow()))
        {
            logger.LogInformation("Message {Id} delivered again, already handled", activity.Id);
            return;
        }

        try
        {
            await HandleMessageAsync(context, cancellationToken);
        }
        catch
        {
            seen.Forget(activity.Conversation?.Id, activity.Id);
            throw;
        }
    }

    private async Task HandleMessageAsync(Context<MessageActivity> context, CancellationToken cancellationToken)
    {
        var activity = context.Activity;
        if (Delivery(activity) is not { } delivery)
        {
            return;
        }

        if (HtmlWidgetHelpers.TryGetWidgetModelContext(activity) is not null)
        {
            logger.LogInformation("The status widget shared context; nothing to do");
            return;
        }

        if (delivery.Turn.Scope != ConversationScope.Personal && !MentionsAgent(activity))
        {
            var readingAlong = watches.Heard(delivery);
            logger.LogInformation("Delivered without an @mention in {Scope}: {Action}", delivery.Turn.Scope,
                readingAlong ? "reading along" : "not reading along here, ignored");
            return;
        }

        var text = (activity.TextWithoutMentions ?? activity.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return;
        }

        logger.LogInformation("Message for the agent in {Scope}", delivery.Turn.Scope);
        delivery = delivery with { Asker = TeamsChannelAccount.FromChannelAccount(activity.From), RequestId = activity.Id };
        await context.TypingAsync(cancellationToken);

        var acknowledged = Acknowledges(delivery) && await sender.ReactAsync(delivery, activity.Id!, Working, add: true, cancellationToken);
        var request = new DirectRequest(delivery.Turn, text, activity.From?.Name ?? "Someone", activity.Id);
        var output = sender.For(delivery);
        if (!queue.TryEnqueue("request", ct => RunAsync(request, delivery, output, acknowledged, ct)))
        {
            await output.PostAsync(Messages.Busy, cancellationToken);
            if (acknowledged)
            {
                await sender.ReactAsync(delivery, activity.Id!, Working, add: false, cancellationToken);
            }
        }
    }

    /// <summary>Says hello when someone adds the agent user to a group chat or a team.</summary>
    private async Task OnConversationUpdateAsync(Context<ConversationUpdateActivity> context, CancellationToken cancellationToken)
    {
        var activity = context.Activity;
        var self = AgenticIdentity.FromAccount(activity.Recipient);
        var added = activity.MembersAdded?.Any(m => m.Id == activity.Recipient?.Id || (self is not null && m.AadObjectId == self.AgenticUserId)) == true;
        if (!added || !options.Value.GreetWhenAdded || Delivery(activity) is not { } delivery || delivery.Turn.Scope == ConversationScope.Personal)
        {
            return;
        }

        logger.LogInformation("Added to a {Scope}", delivery.Turn.Scope);
        await sender.For(delivery).PostAsync(Messages.Welcome(options.Value.Name, delivery.Turn.Scope), cancellationToken);
    }

    private async Task RunAsync(DirectRequest request, TeamsDelivery delivery, IAgentOutput output, bool acknowledged, CancellationToken cancellationToken)
    {
        var done = false;
        try
        {
            await using var workIq = workIqFactory.Create(tokens.For(delivery.Identity));
            await agent.HandleDirectAsync(request, workIq, output, cancellationToken);
            done = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await TryAsync(() => output.PostAsync(AgentReply.Error(Messages.TimedOut), CancellationToken.None));
            throw;
        }
        catch (Exception)
        {
            await TryAsync(() => output.PostAsync(AgentReply.Error(Messages.SomethingWentWrong), CancellationToken.None));
            throw;
        }
        finally
        {
            if (acknowledged && delivery.RequestId is { } requestId)
            {
                await TryAsync(() => sender.ReactAsync(delivery, requestId, Working, add: false, CancellationToken.None));
                if (done)
                {
                    await TryAsync(() => sender.ReactAsync(delivery, requestId, Done, add: true, CancellationToken.None));
                }
            }
        }
    }

    /// <summary>
    /// The draft card's Universal Actions. "Approve" answers right away with the card in its approved state and posts
    /// the slide text, ready to paste, on the queue. "Discard" drops the draft and removes its message.
    /// </summary>
    private async Task<InvokeResponse> OnCardActionAsync(Context<InvokeActivity<AdaptiveCardActionValue>> context, CancellationToken cancellationToken)
    {
        var activity = context.Activity;
        var action = activity.Value?.Action;
        if (Delivery(activity) is not { } delivery || action is null)
        {
            return AdaptiveCardResponse.CreateMessageResponse(Messages.DraftOutOfDate);
        }

        var actor = activity.From?.Name;
        var reference = new DraftRef(DraftCards.Str(action.Data, DraftCards.DraftKey), null);
        logger.LogInformation("Card action '{Verb}' in {Scope}", action.Verb, delivery.Turn.Scope);
        try
        {
            switch (action.Verb)
            {
                case DraftCards.ApproveVerb:
                    if (agent.Draft(delivery.Turn) is not { CanAct: true } draft || !draft.Matches(reference))
                    {
                        return AdaptiveCardResponse.CreateMessageResponse(Messages.DraftOutOfDate);
                    }

                    if (!queue.TryEnqueue("approve draft", ct => agent.ApproveDraftAsync(delivery.Turn, reference, actor, sender.For(delivery), ct)))
                    {
                        return AdaptiveCardResponse.CreateMessageResponse(Messages.Busy);
                    }

                    return AdaptiveCardResponse.CreateCardResponse(Json(DraftCards.Card(draft with { Status = DraftStatus.Posted, ActedBy = actor })));

                case DraftCards.DiscardVerb:
                    var change = await agent.DiscardDraftAsync(delivery.Turn, reference, actor, cancellationToken);
                    if (change.Draft is not { } discarded)
                    {
                        return AdaptiveCardResponse.CreateMessageResponse(change.Problem ?? Messages.DraftOutOfDate);
                    }

                    if ((discarded.MessageId ?? TeamsTurn.RepliedToId(activity)) is { } messageId)
                    {
                        var output = sender.For(delivery);
                        queue.TryEnqueue("remove draft", async ct =>
                        {
                            await Task.Delay(DeleteDelay, ct);
                            await output.DeleteAsync(messageId, ct);
                        });
                    }

                    return AdaptiveCardResponse.CreateMessageResponse(Messages.DraftDiscarded(actor));

                default:
                    logger.LogWarning("Unknown card action '{Verb}'", action.Verb);
                    return AdaptiveCardResponse.CreateMessageResponse(Messages.DraftOutOfDate);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Card action '{Verb}' failed", action.Verb);
            return AdaptiveCardResponse.CreateMessageResponse(Messages.SomethingWentWrong);
        }
    }

    /// <summary>The draft card's Edit button: opens a dialog with the draft's lines.</summary>
    private Task<InvokeResponse<TaskModuleResponse>> OnTaskFetchAsync(Context<InvokeActivity<TaskModuleRequest>> context, CancellationToken cancellationToken)
    {
        var activity = context.Activity;
        var data = DraftCards.AsJson(activity.Value?.Data);
        if (DraftCards.Str(data, DraftCards.ActionKey) != DraftCards.EditAction || Delivery(activity) is not { } delivery)
        {
            return Task.FromResult(DialogMessage(Messages.DraftOutOfDate));
        }

        var reference = new DraftRef(DraftCards.Str(data, DraftCards.DraftKey), null);
        if (agent.Draft(delivery.Turn) is not { CanAct: true } draft || !draft.Matches(reference))
        {
            return Task.FromResult(DialogMessage(Messages.DraftOutOfDate));
        }

        logger.LogInformation("Opening the draft editor in {Scope}", delivery.Turn.Scope);
        var form = TeamsAttachment.CreateBuilder().WithAdaptiveCard(Json(DraftCards.EditForm(draft))).Build();
        return Task.FromResult(new TaskModuleResponseBuilder()
            .WithType(TaskModuleResponseType.Continue)
            .WithTitle(draft.Title)
            .WithCard(form)
            .WithHeight(TaskModuleSizes.Large)
            .WithWidth(TaskModuleSizes.Medium)
            .Build());
    }

    /// <summary>The edit dialog's Save button: revises the draft and updates its card.</summary>
    private async Task<InvokeResponse<TaskModuleResponse>> OnTaskSubmitAsync(Context<InvokeActivity<TaskModuleRequest>> context, CancellationToken cancellationToken)
    {
        var activity = context.Activity;
        var data = DraftCards.AsJson(activity.Value?.Data);
        if (DraftCards.Str(data, DraftCards.ActionKey) != DraftCards.SaveAction || Delivery(activity) is not { } delivery)
        {
            return DialogMessage(Messages.DraftOutOfDate);
        }

        try
        {
            var reference = new DraftRef(DraftCards.Str(data, DraftCards.DraftKey), null);
            var change = await agent.ReviseDraftAsync(delivery.Turn, reference, DraftCards.ReadEditForm(data), activity.From?.Name, sender.For(delivery), cancellationToken);
            return change.Problem is { } problem ? DialogMessage(problem) : CloseDialog;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Saving the draft edits failed");
            return DialogMessage(Messages.SomethingWentWrong);
        }
    }

    /// <summary>The thumbs up or down under an answer or summary. Only the rating and the comment's length are logged.</summary>
    private Task<InvokeResponse> OnFeedbackAsync(Context<InvokeActivity<MessageSubmitFeedbackValue>> context, CancellationToken cancellationToken)
    {
        var value = context.Activity.Value;
        logger.LogInformation("Feedback on message {MessageId}: {Reaction}, comment of {Length} chars",
            TeamsTurn.RepliedToId(context.Activity), value?.Reaction ?? "none", FeedbackText(value?.Feedback)?.Length ?? 0);
        return Task.FromResult(InvokeResponse.Ok());
    }

    /// <summary>Teams sends the comment as a JSON string, {"feedbackText": "..."}.</summary>
    internal static string? FeedbackText(string? feedback)
    {
        if (string.IsNullOrWhiteSpace(feedback))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(feedback);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("feedbackText", out var text) && text.ValueKind == JsonValueKind.String
                    ? text.GetString()
                    : feedback;
        }
        catch (JsonException)
        {
            return feedback;
        }
    }

    /// <summary>A like or check mark on the waiting draft approves it, the same as its "Approve" button.</summary>
    private Task OnReactionAsync(Context<MessageReactionActivity> context, CancellationToken cancellationToken)
    {
        var activity = context.Activity;
        var added = activity.ReactionsAdded?.Select(r => r.Type?.Value ?? "?").ToList() ?? [];
        var removed = activity.ReactionsRemoved?.Select(r => r.Type?.Value ?? "?").ToList() ?? [];
        // Teams sends the reacted message's ID as replyToId and as the activity's own ID.
        var reactedTo = TeamsTurn.RepliedToId(activity) ?? activity.Id;
        logger.LogInformation("Reactions on message {MessageId}: added [{Added}], removed [{Removed}]",
            reactedTo, string.Join(", ", added), string.Join(", ", removed));

        if (Delivery(activity) is not { } delivery
            || reactedTo is not { } messageId
            || string.Equals(activity.From?.Id, activity.Recipient?.Id, StringComparison.OrdinalIgnoreCase)
            || !added.Any(t => t == ReactionType.Like.Value || t == ReactionType.Checkmark.Value || t == Done.Value))
        {
            return Task.CompletedTask;
        }

        if (agent.Draft(delivery.Turn) is not { CanAct: true } draft || draft.MessageId != messageId)
        {
            logger.LogInformation("The reaction isn't on a draft that's waiting (draft message {DraftId})", agent.Draft(delivery.Turn)?.MessageId ?? "none");
            return Task.CompletedTask;
        }

        var actor = activity.From?.Name;
        logger.LogInformation("Draft approved with a reaction");
        queue.TryEnqueue("approve draft", ct => agent.ApproveDraftAsync(delivery.Turn, new DraftRef(draft.Stamp, messageId), actor, sender.For(delivery), ct));
        return Task.CompletedTask;
    }

    /// <summary>The status widget's Refresh button calls this tool for the incident's latest status.</summary>
    private Task<InvokeResponse> OnWidgetToolAsync(Context<InvokeActivity<CallToolRequest>> context, CancellationToken cancellationToken)
    {
        var request = context.Activity.Value;
        logger.LogInformation("Widget called tool '{Tool}'", request?.Name);
        if (request?.Name != IncidentWidget.ToolName)
        {
            return Task.FromResult<InvokeResponse>(InvokeResponse.Ok(HtmlWidgetCallToolResponse.FromError($"Unknown tool '{request?.Name}'.")));
        }

        var result = agent.Incident(IncidentWidget.RequestedId(request.Arguments)) is { } note
            ? HtmlWidgetCallToolResponse.FromText(IncidentWidget.ToolResult(note))
            : HtmlWidgetCallToolResponse.FromError("I don't have notes on that incident right now.");
        return Task.FromResult<InvokeResponse>(InvokeResponse.Ok(result));
    }

    /// <summary>The agent user was created, enabled, disabled and so on. Logged so you can see them arrive.</summary>
    private Task OnLifecycleAsync(Context<AgentLifecycleEventActivity> context, CancellationToken cancellationToken)
    {
        logger.LogInformation("Agent lifecycle event: {Type}", context.Activity.ValueType?.Value ?? "unknown");
        return Task.CompletedTask;
    }

    private Task OnEventAsync(Context<EventActivity> context, CancellationToken cancellationToken)
    {
        if (context.Activity.Name != EventName.AgentLifecycle)
        {
            logger.LogInformation("Event '{Name}' from the '{Channel}' channel", context.Activity.Name?.Value, context.Activity.ChannelId);
        }

        return Task.CompletedTask;
    }


    /// <summary>Whether to mark the request with reactions: in group chats and channels, where others see it was picked up.</summary>
    private bool Acknowledges(TeamsDelivery delivery) =>
        features.Value.ReactionAck && delivery.Turn.Scope != ConversationScope.Personal && delivery.RequestId is not null;

    /// <summary>Who the agent is in this activity and where its replies go; null for anything that isn't Teams traffic for an agent user.</summary>
    private TeamsDelivery? Delivery(TeamsActivity activity)
    {
        if (!string.Equals(activity.ChannelId, TeamsChannel, StringComparison.OrdinalIgnoreCase))
        {
            // The shape only, never the content, to learn what other channels send.
            logger.LogInformation("Ignoring a {Type} activity from the '{Channel}' channel (properties [{Properties}], entities [{Entities}], attachments [{Attachments}])",
                activity.Type, activity.ChannelId,
                string.Join(", ", activity.Properties.Keys),
                string.Join(", ", activity.Entities?.Select(e => e.Type) ?? []),
                activity is MessageActivity message ? string.Join(", ", message.Attachments?.Select(a => a.ContentType.Value) ?? []) : string.Empty);
            return null;
        }

        var identity = AgenticIdentity.FromAccount(activity.Recipient);
        if (identity?.AgenticUserId is not { Length: > 0 } selfId || activity.ServiceUrl is null)
        {
            logger.LogWarning("Ignoring an activity that isn't addressed to an agent user");
            return null;
        }

        var turnInfo = TeamsTurn.From(activity);
        var turn = new AgentTurn(ConversationLocator.Locate(turnInfo), ConversationLocator.ScopeOf(turnInfo), selfId, options.Value.ZoneFor(turnInfo.TimeZone));
        return new TeamsDelivery(turnInfo.ConversationId, activity.ServiceUrl, identity, turn);
    }

    /// <summary>Whether the message @mentions the agent user (the recipient), as opposed to someone else or nobody.</summary>
    private static bool MentionsAgent(MessageActivity activity) =>
        activity.GetMentions().Any(m => string.Equals(m.Mentioned?.Id, activity.Recipient?.Id, StringComparison.OrdinalIgnoreCase));

    private static InvokeResponse<TaskModuleResponse> DialogMessage(string text) =>
        new TaskModuleResponseBuilder().WithType(TaskModuleResponseType.Message).WithMessage(text).Build();

    /// <summary>An empty response closes the dialog. The card in the chat already shows the change.</summary>
    private static InvokeResponse<TaskModuleResponse> CloseDialog => new(200);

    private static JsonElement Json(JsonObject card) => JsonSerializer.SerializeToElement(card);

    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Follow-up step failed");
        }
    }
}
