using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentAgent.Agent;
using IncidentAgent.Conversations;
using IncidentAgent.Knowledge;
using IncidentAgent.Llm;
using IncidentAgent.Shiproom;
using IncidentAgent.Teams;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Core.Schema;

namespace IncidentAgent.Tests;

/// <summary>The Teams message features: citations, the draft card and its actions, streaming, the status widget.</summary>
public class TeamsFeatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 17, 0, 0, TimeSpan.Zero);
    private static readonly AgentTurn Turn = new(new ConversationTarget.GroupChat("19:chat@thread.v2"), ConversationScope.GroupChat, Graph.Self, TimeZoneInfo.Utc);
    private static readonly DeckFile Deck = new("b!drive", "item1", "Helpdesk Shiproom - 2026-10-11.pptx", "https://contoso.sharepoint.com/deck", null);
    private static readonly Meeting Shiproom = new("ev1", "Helpdesk Shiproom", Now.AddDays(1), Now.AddDays(1).AddMinutes(30), null, [], [], null);

    private const string Runbook = """
        # Runbook

        ### TB-004: Bot gets 401 or 403 from Bot Connector when replying
        **Symptoms:** Reply calls fail.
        **Keywords:** 401, 403, Bot Connector, Unauthorized, a keyword that is much too long to show, MicrosoftAppId
        **Cause:** The bot credentials don't match the **Azure Bot** registration.
        **Fix:**
        1. Check the app ID.

        ### TB-005: Messaging endpoint is wrong or not reachable
        **Symptoms:** Nothing arrives.
        **Keywords:** endpoint, tunnel
        **Cause:** The messaging endpoint points somewhere else.
        **Fix:**
        1. Fix the endpoint.
        """;

    [Fact]
    public void Cites_runbook_cases_in_order_of_first_mention()
    {
        var (text, citations) = RunbookCitations.Cite("INC-2304 looks like **TB-005**, then TB-004. TB-005 again, and TB-999.", KnowledgeBase.Parse(Runbook));

        Assert.Equal("INC-2304 looks like **TB-005** [1], then TB-004 [2]. TB-005 again, and TB-999.", text);
        Assert.Equal([1, 2], citations.Select(c => c.Position));
        Assert.Equal("TB-005: Messaging endpoint is wrong or not reachable", citations[0].Title);
        Assert.Equal("The bot credentials don't match the Azure Bot registration.", citations[1].Abstract);
        Assert.Equal(["401", "403", "Bot Connector"], citations[1].Keywords);
    }

    [Fact]
    public void Leaves_text_without_runbook_cases_alone()
    {
        var (text, citations) = RunbookCitations.Cite("Nothing to cite in INC-2304.", KnowledgeBase.Parse(Runbook));

        Assert.Equal("Nothing to cite in INC-2304.", text);
        Assert.Empty(citations);
    }

    [Fact]
    public void Finds_the_reacted_message_even_though_the_SDK_drops_replyToId()
    {
        var reaction = MessageReactionActivity.FromActivity(Incoming("""
            {"type":"messageReaction","id":"1001","channelId":"msteams","replyToId":"1001",
             "conversation":{"id":"19:chat@thread.v2","conversationType":"groupChat"},"reactionsAdded":[{"type":"like"}]}
            """));

        Assert.Null(reaction.ReplyToId);
        Assert.Equal("1001", TeamsTurn.RepliedToId(reaction));
    }

    [Fact]
    public void Finds_the_message_a_feedback_invoke_is_about()
    {
        var feedback = InvokeActivity.FromActivity(Incoming("""
            {"type":"invoke","name":"message/submitAction","id":"f:abc","channelId":"msteams","replyToId":"2002",
             "conversation":{"id":"19:chat@thread.v2","conversationType":"groupChat"},
             "value":{"actionName":"feedback","actionValue":{"reaction":"like","feedback":"{\"feedbackText\":\"Helpful\"}"}}}
            """));

        Assert.Equal("2002", TeamsTurn.RepliedToId(feedback));
        Assert.Equal("Helpful", AgentHandlers.FeedbackText("""{"feedbackText":"Helpful"}"""));
        Assert.Equal("plain", AgentHandlers.FeedbackText("plain"));
        Assert.Null(AgentHandlers.FeedbackText(""));
    }

    /// <summary>An activity as the handlers get it: through <see cref="KeepReplyToId"/> first.</summary>
    private static CoreActivity Incoming(string json)
    {
        var activity = CoreActivity.FromJsonString(json);
        new KeepReplyToId().OnTurnAsync(null!, activity, _ => Task.CompletedTask).GetAwaiter().GetResult();
        return activity;
    }

    [Fact]
    public void A_waiting_draft_card_has_approve_edit_and_discard()
    {
        var draft = Draft();

        var card = DraftCards.Card(draft);

        var actions = card["actions"]!.AsArray();
        Assert.Equal(["Approve", "Edit", "Discard"], actions.Select(a => (string)a!["title"]!));
        Assert.Equal(["Action.Execute", "Action.Submit", "Action.Execute"], actions.Select(a => (string)a!["type"]!));
        Assert.Equal(DraftCards.ApproveVerb, (string)actions[0]!["verb"]!);
        Assert.Equal(draft.Stamp, (string)actions[0]!["data"]![DraftCards.DraftKey]!);
        Assert.Equal("task/fetch", (string)actions[1]!["data"]!["msteams"]!["type"]!);
        Assert.Equal(DraftCards.EditAction, (string)actions[1]!["data"]![DraftCards.ActionKey]!);
        Assert.Equal(DraftCards.DiscardVerb, (string)actions[2]!["verb"]!);
        Assert.Contains("- **Impact:** 40 chats.", card.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    [Fact]
    public void An_approved_draft_links_the_deck_and_one_without_a_deck_can_still_be_approved()
    {
        var posted = DraftCards.Card(Draft() with { Status = DraftStatus.Posted, ActedBy = "Maya" });
        var noDeck = DraftCards.Card(Draft() with { Deck = null });

        var open = Assert.Single(posted["actions"]!.AsArray());
        Assert.Equal("Action.OpenUrl", (string)open!["type"]!);
        Assert.Equal(Deck.WebUrl, (string)open["url"]!);
        Assert.Equal("Good", (string)posted["body"]!.AsArray()[^1]!["color"]!);
        Assert.Equal(["Approve", "Edit", "Discard"], noDeck["actions"]!.AsArray().Select(a => (string)a!["title"]!));
    }

    [Fact]
    public void The_edit_form_reads_back_in_line_order()
    {
        var form = DraftCards.EditForm(Draft());

        var inputs = form["body"]!.AsArray().Where(b => (string)b!["type"]! == "Input.Text").ToList();
        Assert.Equal(2 + DraftCards.SpareLines, inputs.Count);
        Assert.Equal("INC-2304 · Staging (Sev 3) — Mitigated", (string)inputs[0]!["value"]!);

        var submitted = FakeWorkIq.Json("""{"line10":"ten","line2":"two","line0":"heading","action":"saveDraft","draft":"1","lineX":"skip"}""");
        Assert.Equal(["heading", "two", "ten"], DraftCards.ReadEditForm(submitted));
    }

    [Fact]
    public void The_draft_says_where_it_stands()
    {
        Assert.Equal("Approve it and I'll post the text ready to paste into [Helpdesk Shiproom - 2026-10-11.pptx](https://contoso.sharepoint.com/deck).", Messages.DraftState(Draft()));
        Assert.StartsWith("Edited by Maya. Approve it", Messages.DraftState(Draft() with { ActedBy = "Maya" }));
        Assert.Equal("The invite doesn't link this week's deck. Approve it and I'll post the text ready to paste.", Messages.DraftState(Draft() with { Deck = null }));
        Assert.StartsWith("Approved by Maya. The text is in my next message", Messages.DraftState(Draft() with { Status = DraftStatus.Posted, ActedBy = "Maya" }));
        Assert.Equal("Discarded by Maya.", Messages.DraftState(Draft() with { Status = DraftStatus.Discarded, ActedBy = "Maya" }));
    }

    [Fact]
    public async Task Editing_the_draft_keeps_the_deck_formatting_and_updates_its_card()
    {
        var (agent, _, memory) = Create();
        var draft = Draft() with { MessageId = "m1" };
        memory.For(Turn.Key).Draft = draft;
        var output = new RecordingOutput();

        var change = await agent.ReviseDraftAsync(Turn, new DraftRef(draft.Stamp, null), ["New heading", "", "Impact: 12 chats."], "Maya", output, default);

        var revised = Assert.IsType<ShiproomDraft>(change.Draft);
        Assert.Equal([("New heading", 0, true), ("Impact: 12 chats.", 1, false)], revised.Lines.Select(l => (l.Text, l.Level, l.Bold)));
        Assert.Equal(draft.Stamp, revised.Stamp);
        Assert.Equal("Maya", revised.ActedBy);
        var (messageId, reply) = Assert.Single(output.Updates);
        Assert.Equal("m1", messageId);
        Assert.Same(revised, reply.Draft);
        Assert.Empty(output.Replies);
        Assert.Same(revised, memory.For(Turn.Key).Draft);
    }

    [Fact]
    public async Task An_edited_draft_is_posted_again_when_its_message_is_gone()
    {
        var (agent, _, memory) = Create();
        var draft = Draft() with { MessageId = "m1" };
        memory.For(Turn.Key).Draft = draft;
        var output = new RecordingOutput { CanUpdate = false };

        var change = await agent.ReviseDraftAsync(Turn, new DraftRef(draft.Stamp, null), ["Heading"], "Maya", output, default);

        Assert.Equal(ReplyKind.Draft, Assert.Single(output.Replies).Kind);
        Assert.Equal("1001", change.Draft!.MessageId);
        Assert.Equal("1001", memory.For(Turn.Key).Draft!.MessageId);
    }

    [Fact]
    public async Task Actions_on_an_older_card_or_with_nothing_left_are_refused()
    {
        var (agent, _, memory) = Create();
        var draft = Draft();
        memory.For(Turn.Key).Draft = draft;

        Assert.Equal(Messages.DraftOutOfDate, (await agent.ReviseDraftAsync(Turn, new DraftRef("123", null), ["x"], null, new RecordingOutput(), default)).Problem);
        Assert.Equal(Messages.DraftEmpty, (await agent.ReviseDraftAsync(Turn, new DraftRef(draft.Stamp, null), [" ", ""], null, new RecordingOutput(), default)).Problem);
        Assert.Equal(Messages.DraftOutOfDate, (await agent.DiscardDraftAsync(Turn, new DraftRef("123", null), null, default)).Problem);
        Assert.Same(draft, memory.For(Turn.Key).Draft);
    }

    [Fact]
    public async Task Discarding_drops_the_draft_once()
    {
        var (agent, _, memory) = Create();
        var draft = Draft();
        memory.For(Turn.Key).Draft = draft;

        var change = await agent.DiscardDraftAsync(Turn, new DraftRef(draft.Stamp, null), "Maya", default);
        var again = await agent.DiscardDraftAsync(Turn, new DraftRef(draft.Stamp, null), "Maya", default);

        Assert.Equal(DraftStatus.Discarded, change.Draft!.Status);
        Assert.Equal("Maya", change.Draft.ActedBy);
        Assert.Null(memory.For(Turn.Key).Draft);
        Assert.Equal(Messages.DraftOutOfDate, again.Problem);
    }

    [Fact]
    public async Task Approving_the_draft_marks_its_card_and_posts_the_text_ready_to_paste()
    {
        var (agent, _, memory) = Create();
        var draft = Draft() with { MessageId = "m1" };
        memory.For(Turn.Key).Draft = draft;
        var output = new RecordingOutput();

        await agent.ApproveDraftAsync(Turn, new DraftRef(draft.Stamp, "m1"), "Maya", output, default);

        var (messageId, update) = Assert.Single(output.Updates);
        Assert.Equal("m1", messageId);
        Assert.Equal(DraftStatus.Posted, update.Draft!.Status);
        Assert.Equal("Maya", update.Draft.ActedBy);
        var reply = Assert.Single(output.Replies);
        Assert.Equal(ReplyKind.PasteReady, reply.Kind);
        Assert.Contains("[Helpdesk Shiproom - 2026-10-11.pptx](https://contoso.sharepoint.com/deck)", reply.Markdown);
        Assert.Contains("Impact: 40 chats.", reply.Markdown);
        Assert.Null(memory.For(Turn.Key).Draft);
    }

    [Fact]
    public async Task Asking_to_add_it_to_the_deck_posts_the_text_ready_to_paste()
    {
        var (agent, chat, memory) = Create();
        chat.Reply("""{"intent":"add_to_deck","confidence":0.95,"reason":"approved"}""");
        var draft = Draft() with { MessageId = "m1" };
        memory.For(Turn.Key).Draft = draft;
        var output = new RecordingOutput();

        await agent.HandleDirectAsync(new DirectRequest(Turn, "Looks good, can you add it to the deck?", "Maya", "m3"), ChatWorkIq(), output, default);

        Assert.Equal(DraftStatus.Posted, Assert.Single(output.Updates).Reply.Draft!.Status);
        var reply = Assert.Single(output.Replies);
        Assert.Equal(ReplyKind.PasteReady, reply.Kind);
        Assert.StartsWith("Here's the Incident updates slide for", reply.Markdown);
        Assert.Null(memory.For(Turn.Key).Draft);
    }

    [Fact]
    public async Task Asking_to_add_it_to_the_deck_without_a_draft_says_so()
    {
        var (agent, chat, _) = Create();
        chat.Reply("""{"intent":"add_to_deck","confidence":0.95,"reason":"approved"}""");
        var output = new RecordingOutput();

        await agent.HandleDirectAsync(new DirectRequest(Turn, "add it to the deck", "Maya", "m3"), ChatWorkIq(), output, default);

        Assert.Equal(Messages.NoDraft, Assert.Single(output.Replies).Markdown);
        Assert.Empty(output.Updates);
    }

    [Fact]
    public async Task A_reaction_on_a_different_message_does_nothing()
    {
        var (agent, _, memory) = Create();
        var draft = Draft() with { MessageId = "m1" };
        memory.For(Turn.Key).Draft = draft;
        var output = new RecordingOutput();

        await agent.ApproveDraftAsync(Turn, new DraftRef(null, "m2"), "Maya", output, default);

        Assert.Empty(output.Updates);
        Assert.Same(draft, memory.For(Turn.Key).Draft);
    }

    [Fact]
    public void Answers_and_summaries_carry_the_ai_label_feedback_and_citations()
    {
        var sender = Sender(new TeamsFeatureOptions { SensitivityLabel = "Confidential", SensitivityLabelDescription = "Team only" });
        var reply = new AgentReply("See TB-004 [1].", ReplyKind.Answer)
        {
            Citations = [new Citation(1, "TB-004: 401s", "Credentials don't match.", ["401"])],
            Suggestions = ["Summarize INC-2304"],
        };

        var personal = Wire(sender.Render(reply, ConversationScope.Personal, "8:orgid:maya"));
        var group = Wire(sender.Render(reply, ConversationScope.GroupChat, "8:orgid:maya"));
        var notice = Wire(sender.Render(AgentReply.Notice("On it."), ConversationScope.Personal));

        Assert.Contains("AIGeneratedContent", personal);
        Assert.Contains("\"feedbackLoopEnabled\":true", personal);
        Assert.Contains("TB-004: 401s", personal);
        Assert.Contains("Confidential", personal);
        Assert.Contains("Summarize INC-2304", personal);
        Assert.Equal("8:orgid:maya", (string)JsonNode.Parse(personal)!["suggestedActions"]!["to"]![0]!);
        Assert.DoesNotContain("Summarize INC-2304", group);
        Assert.Contains("AIGeneratedContent", notice);
        Assert.DoesNotContain("feedbackLoopEnabled", notice);
        Assert.DoesNotContain("Confidential", notice);
    }

    [Fact]
    public void The_label_and_citations_share_one_root_message_entity()
    {
        var sender = Sender(new TeamsFeatureOptions { SensitivityLabel = "General" });
        var reply = new AgentReply("See TB-004 [1] and TB-005 [2].", ReplyKind.Answer)
        {
            Citations = [new Citation(1, "TB-004", "a", []), new Citation(2, "TB-005", "b", [])],
        };

        var entities = JsonNode.Parse(Wire(sender.Render(reply, ConversationScope.Personal)))!["entities"]!.AsArray();

        var root = Assert.Single(entities, e => (string?)e!["type"] == "https://schema.org/Message");
        Assert.Equal(("Message", "https://schema.org"), ((string)root!["@type"]!, (string)root["@context"]!));
        Assert.Equal(2, root["citation"]!.AsArray().Count);
        Assert.Equal("General", (string)root["usageInfo"]!["name"]!);
        Assert.Equal("CreativeWork", (string)root["usageInfo"]!["@type"]!);
    }

    [Fact]
    public void Drafts_render_as_a_card_unless_turned_off()
    {
        var reply = IncidentAgentService.DraftReply(Draft());

        var card = Sender(new TeamsFeatureOptions()).Render(reply, ConversationScope.GroupChat);
        var text = Sender(new TeamsFeatureOptions { DraftCard = false }).Render(reply, ConversationScope.GroupChat);

        Assert.Contains("application/vnd.microsoft.card.adaptive", Wire(card));
        Assert.Equal(DraftCards.Summary(reply.Draft!), (string)JsonNode.Parse(Wire(card))!["summary"]!);
        Assert.StartsWith(reply.Draft!.Title + ": ", DraftCards.Summary(reply.Draft));
        Assert.Null(card.Text);
        Assert.Null(text.Attachments);
        Assert.Contains("Approve it and I'll post", text.Text);
    }

    [Fact]
    public void Summaries_carry_a_status_widget_when_turned_on()
    {
        var note = Note();
        var reply = new AgentReply(note.Summary, ReplyKind.Summary) { Incident = note };

        var widget = Sender(new TeamsFeatureOptions { HtmlWidget = true, WidgetDomain = "https://iris.example.com" }).Render(reply, ConversationScope.GroupChat);
        var plain = Sender(new TeamsFeatureOptions()).Render(reply, ConversationScope.GroupChat);

        Assert.StartsWith(note.Summary, widget.Text);
        Assert.Contains("```html-widget", widget.Text);
        Assert.Contains(IncidentWidget.ToolName, widget.Text);
        Assert.Equal(note.Summary, plain.Text);
    }

    [Fact]
    public void The_widget_reads_severity_and_status_from_the_summary_header()
    {
        var status = IncidentWidget.Status(Note() with { Updates = ["Rolled back at 10:40."] });

        Assert.Equal(("INC-2304", "Sev 2", "Mitigated", "Rolled back at 10:40."), (status.Id, status.Severity, status.Status, status.Latest));
        var json = JsonNode.Parse(IncidentWidget.ToolResult(Note()))!;
        Assert.Equal("Sev 2", (string)json["severity"]!);
        Assert.Equal("INC-2304", IncidentWidget.RequestedId(FakeWorkIq.Json("""{"id":"INC-2304"}""")));
    }

    [Fact]
    public void Widget_summaries_keep_their_line_breaks_in_channels()
    {
        const string summary = "**INC-2304 (Sev 2) — Resolved**\nImpact: pilot users.\nNext:\n- Maya: postmortem\n- Daniel: alerts\n  owner review\n\n```\na\nb\n```";

        var blocks = IncidentWidget.Blocks(summary);

        Assert.Equal(
            "**INC-2304 (Sev 2) — Resolved**\n\nImpact: pilot users.\n\nNext:\n\n- Maya: postmortem\n- Daniel: alerts\n  owner review\n\n```\na\nb\n```",
            blocks);
        Assert.Equal(blocks, IncidentWidget.Blocks(blocks));
    }

    [Fact]
    public void Header_parsing_ignores_other_parentheses_in_the_title()
    {
        var note = new IncidentNote("INC-1", "Sync (EU)", "**INC-1 · Sync (EU) (Sev 3) — Investigating**\n\nBody", Now);

        var status = IncidentWidget.Status(note);

        Assert.Equal(("Sev 3", "Investigating"), (status.Severity, status.Status));
    }

    [Theory]
    [InlineData(StreamingScope.Off, ConversationScope.Personal, false)]
    [InlineData(StreamingScope.Personal, ConversationScope.Personal, true)]
    [InlineData(StreamingScope.Personal, ConversationScope.Channel, false)]
    [InlineData(StreamingScope.All, ConversationScope.GroupChat, true)]
    public void Streams_where_configured(StreamingScope streaming, ConversationScope scope, bool expected)
    {
        Assert.Equal(expected, new TeamsFeatureOptions { Streaming = streaming }.StreamsIn(scope));
    }

    [Fact]
    public async Task A_streamed_summary_shows_each_step_before_the_reply()
    {
        var (agent, chat, _) = Create();
        chat.Reply("""{"intent":"summarize","confidence":0.95,"reason":"asked"}""")
            .Reply("""{"incident":"INC-2304","title":"Staging bot 401s","severity":"Sev 2","status":"Mitigated","summary":"Rolled back, and the 401s stopped."}""");
        var stream = new RecordingStream();
        var output = new RecordingOutput { Stream = stream };

        await agent.HandleDirectAsync(new DirectRequest(Turn, "catch me up", "Maya", "m3"), ChatWorkIq(), output, default);

        Assert.Equal(["Reading this chat", Messages.CheckingRunbook, "Pulling the incident together from all 2 messages in this chat"], stream.Informed);
        Assert.Equal(ReplyKind.Summary, Assert.Single(output.Replies).Kind);
    }

    [Fact]
    public async Task Stops_working_when_the_person_stops_the_reply()
    {
        var (agent, chat, _) = Create();
        chat.Reply("""{"intent":"summarize","confidence":0.95,"reason":"asked"}""");
        var output = new RecordingOutput { Stream = new RecordingStream { Cancelled = true } };

        await agent.HandleDirectAsync(new DirectRequest(Turn, "catch me up", "Maya", "m3"), ChatWorkIq(), output, default);

        Assert.Empty(output.Replies);
        Assert.Single(chat.Requests);
    }

    [Theory]
    [InlineData(ConversationScope.Personal, "Reading our chat")]
    [InlineData(ConversationScope.GroupChat, "Reading this chat")]
    [InlineData(ConversationScope.Channel, "Reading this thread")]
    public void The_first_step_names_the_conversation(ConversationScope scope, string expected)
    {
        Assert.Equal(expected, Messages.Reading(scope));
    }

    [Fact]
    public async Task Answers_without_a_missing_runbook_and_says_where_it_should_be()
    {
        var (agent, chat, _) = Create();
        chat.Reply("""{"intent":"answer","confidence":0.9,"reason":"a greeting"}""").Reply("Hi Maya!");
        var output = new RecordingOutput();

        await agent.HandleDirectAsync(new DirectRequest(Turn, "hi", "Maya", "m3"), ChatWorkIq(runbook: false), output, default);

        var reply = Assert.Single(output.Replies);
        Assert.Equal(ReplyKind.Answer, reply.Kind);
        Assert.StartsWith("Hi Maya!", reply.Markdown);
        Assert.Contains(new RunbookOptions().Path, reply.Markdown);
        Assert.Contains("seed_onedrive.py", reply.Markdown);
        Assert.Contains("runbook isn't available", chat.Requests[^1].Last().Text);
    }

    [Fact]
    public async Task Summarizes_without_a_missing_runbook()
    {
        var (agent, chat, _) = Create();
        chat.Reply("""{"intent":"summarize","confidence":0.95,"reason":"asked"}""")
            .Reply("""{"incident":"INC-2304","title":"Staging bot 401s","severity":"Sev 2","status":"Mitigated","summary":"Rolled back, and the 401s stopped."}""");
        var output = new RecordingOutput();

        await agent.HandleDirectAsync(new DirectRequest(Turn, "catch me up", "Maya", "m3"), ChatWorkIq(runbook: false), output, default);

        var reply = Assert.Single(output.Replies);
        Assert.Equal(ReplyKind.Summary, reply.Kind);
        Assert.Contains("Rolled back", reply.Markdown);
        Assert.Contains("seed_onedrive.py", reply.Markdown);
    }

    [Fact]
    public async Task Other_work_iq_failures_keep_the_general_message()
    {
        var (agent, chat, _) = Create();
        chat.Reply("""{"intent":"answer","confidence":0.9,"reason":"a greeting"}""");
        var output = new RecordingOutput();
        var workIq = ChatWorkIq(runbook: false).BlobError(WorkIqPaths.MyDriveFileContent(new RunbookOptions().Path), WorkIqFailure.Unavailable);

        await agent.HandleDirectAsync(new DirectRequest(Turn, "hi", "Maya", "m3"), workIq, output, default);

        Assert.Equal("I couldn't reach Work IQ just now. Try again in a minute.", Assert.Single(output.Replies).Markdown);
    }

    [Fact]
    public async Task A_missing_runbook_is_reported_with_its_path_and_not_cached()
    {
        var path = new RunbookOptions().Path;
        var store = new RunbookStore(Options.Create(new RunbookOptions()), new FixedClock(Now), NullLogger<RunbookStore>.Instance);
        var workIq = new FakeWorkIq();

        var missing = await Assert.ThrowsAsync<RunbookNotFoundException>(() => store.GetAsync(Graph.Self, workIq, default));
        await Assert.ThrowsAsync<RunbookNotFoundException>(() => store.GetAsync(Graph.Self, workIq, default));

        Assert.Equal(path, missing.Path);
        Assert.Equal(WorkIqFailure.NotFound, missing.Failure);
        Assert.Equal(2, workIq.Fetched.Count);
        Assert.Contains(path, Messages.WorkIqFailed(missing));
        Assert.Contains("seed_onedrive.py", Messages.WorkIqFailed(missing));
    }

    private static FakeWorkIq ChatWorkIq(bool runbook = true)
    {
        var workIq = new FakeWorkIq().On(
            WorkIqPaths.ChatMessages("19:chat@thread.v2", new ConversationOptions().PageSize),
            JsonSerializer.Serialize(new
            {
                value = new[]
                {
                    Graph.Message("m2", "Maya", Now.AddMinutes(-20), "Rolled back, the 401s are gone"),
                    Graph.Message("m1", "Daniel", Now.AddMinutes(-30), "Staging bot is returning 401s"),
                },
            }));
        return runbook ? workIq.Blob(WorkIqPaths.MyDriveFileContent(new RunbookOptions().Path), System.Text.Encoding.UTF8.GetBytes(Runbook)) : workIq;
    }

    private static ShiproomDraft Draft() =>
        new(Shiproom, Deck, "Incident updates", [new("INC-2304 · Staging (Sev 3) — Mitigated", 0, true), new("Impact: 40 chats.", 1)], "INC-2304", Now)
        {
            Subtitle = "Sun Oct 11, 10:00 AM",
            Footnote = "Format from prior.pptx, slide 3.",
        };

    private static IncidentNote Note() =>
        new("INC-2304", "Staging bot 401s", "**INC-2304 · Staging bot 401s (Sev 2) — Mitigated**\n\n**Impact:** Pilot users.", Now);


    private static TeamsSender Sender(TeamsFeatureOptions features) =>
        new(new WatchRegistry(Options.Create(new WatchOptions()), TimeProvider.System, NullLogger<WatchRegistry>.Instance), Options.Create(features), NullLoggerFactory.Instance);

    private static string Wire(MessageActivityInput activity) => JsonNode.Parse(activity.ToJson())!.ToJsonString();

    private static (IncidentAgentService Agent, FakeChatClient Chat, AgentMemory Memory) Create()
    {
        var chat = new FakeChatClient();
        var memory = new AgentMemory();
        var clock = new FixedClock(Now);
        var agent = new IncidentAgentService(
            chat,
            new RunbookStore(Options.Create(new RunbookOptions()), clock, NullLogger<RunbookStore>.Instance),
            new ConversationReader(Options.Create(new ConversationOptions()), clock, NullLogger<ConversationReader>.Instance),
            new CalendarReader(Options.Create(new ShiproomOptions()), clock, NullLogger<CalendarReader>.Instance),
            new DeckFinder(NullLogger<DeckFinder>.Instance),

            memory,
            Options.Create(new AgentOptions()),
            Options.Create(new RunbookOptions()),
            Options.Create(new ShiproomOptions()),
            Options.Create(new WatchOptions()),
            Options.Create(new LlmOptions { Endpoint = "https://example.openai.azure.com/", Deployment = "test" }),
            clock,
            NullLoggerFactory.Instance);
        return (agent, chat, memory);
    }
}
