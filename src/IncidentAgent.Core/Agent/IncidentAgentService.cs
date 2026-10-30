using System.ComponentModel;
using System.Text;
using System.Text.Json;
using IncidentAgent.Conversations;
using IncidentAgent.Knowledge;
using IncidentAgent.Llm;
using IncidentAgent.Shiproom;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentAgent.Agent;

/// <summary>A message sent to the agent: @mentioned in a group chat or channel, or anything in its 1:1 chat.</summary>
public sealed record DirectRequest(AgentTurn Turn, string Text, string AskerName, string? MessageId);

/// <summary>
/// The incident agent. Everything it reads in Microsoft 365 goes through Work IQ as the agent user: the conversation,
/// its runbook in its own OneDrive, its calendar, and the meeting decks shared with it.
/// </summary>
public sealed class IncidentAgentService(
    IChatClient chat,
    RunbookStore runbooks,
    ConversationReader reader,
    CalendarReader calendar,
    DeckFinder deckFinder,
    AgentMemory memory,
    IOptions<AgentOptions> agentOptions,
    IOptions<RunbookOptions> runbookOptions,
    IOptions<ShiproomOptions> shiproomOptions,
    IOptions<WatchOptions> watchOptions,
    IOptions<LlmOptions> llm,
    TimeProvider clock,
    ILoggerFactory loggers)
{
    public const string CaseToolName = "get_runbook_cases";
    public const string MeetingToolName = "find_meeting";
    private const int MaxCasesPerCall = 3;
    private const int MaxToolRounds = 5;

    /// <summary>Card actions answer within a few seconds, so they don't wait long for the agent to finish something else.</summary>
    private static readonly TimeSpan DraftGateWait = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger = loggers.CreateLogger<IncidentAgentService>();

    private string Name => agentOptions.Value.Name;

    private string Role => agentOptions.Value.Role;

    public async Task HandleDirectAsync(DirectRequest request, IWorkIq workIq, IAgentOutput output, CancellationToken cancellationToken)
    {
        var turn = request.Turn;
        var state = memory.For(turn.Key);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            // Where the channel supports it, the person sees each step the agent takes, then the reply. The first post
            // to the output ends the stream, so every path below posts something.
            var stream = output.OpenStream();
            await InformAsync(stream, Messages.Reading(turn.Scope), cancellationToken);
            var transcript = await ReadAsync(workIq, turn, request.MessageId, cancellationToken);
            var asked = new ChatLine(request.MessageId ?? "request", request.AskerName, clock.GetUtcNow(), request.Text);
            var intent = await ClassifyAsync([.. transcript.Lines.TakeLast(15)], [asked], addressed: true, state.Draft is not null, turn.Zone, cancellationToken);
            _logger.LogInformation("Request in {Scope}: {Intent} ({Confidence:0.00}, {Reason})", turn.Scope, intent.Kind, intent.Confidence, intent.Reason);
            if (stream is { Cancelled: true })
            {
                _logger.LogInformation("The person stopped the request");
                return;
            }

            switch (intent.Kind)
            {
                case AgentIntentKind.Summarize:
                    await SummarizeAsync(workIq, turn, transcript, request.Text, intent, output, stream, cancellationToken);
                    break;
                case AgentIntentKind.ShiproomUpdate:
                    await ShiproomUpdateAsync(workIq, turn, transcript, intent, proactive: false, output, stream, cancellationToken);
                    break;
                case AgentIntentKind.AddToDeck:
                    await PostForDeckAsync(turn, DraftRef.Current, request.AskerName, announce: true, output, cancellationToken);
                    break;
                default:
                    await AnswerAsync(workIq, turn, transcript, request, output, stream, cancellationToken);
                    break;
            }
        }
        catch (WorkIqException ex)
        {
            _logger.LogWarning(ex, "Work IQ failed ({Failure})", ex.Failure);
            await output.PostAsync(AgentReply.Error(Messages.WorkIqFailed(ex)), cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>
    /// Looks at messages nobody addressed to the agent and acts when the team clearly needs something it can do.
    /// Returns whether it acted. Skips the round if the agent is already working in this conversation.
    /// </summary>
    public async Task<bool> HandleObservedAsync(AgentTurn turn, IReadOnlyList<ChatLine> recent, IReadOnlyList<ChatLine> newLines, IWorkIq workIq, IAgentOutput output, CancellationToken cancellationToken)
    {
        var state = memory.For(turn.Key);
        if (!await state.Gate.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            var fresh = newLines.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
            var intent = await ClassifyAsync([.. recent.Where(l => !fresh.Contains(l.Id))], newLines, addressed: false, state.Draft is not null, turn.Zone, cancellationToken);
            var allowed = intent.Kind is AgentIntentKind.ShiproomUpdate or AgentIntentKind.Summarize
                || (intent.Kind is AgentIntentKind.AddToDeck && state.Draft is not null);
            var act = allowed && intent.Confidence >= watchOptions.Value.MinConfidence;
            _logger.LogInformation("Read along in {Scope}: {Count} new messages, intent {Intent} ({Confidence:0.00}, {Reason}); {Decision}",
                turn.Scope, newLines.Count, intent.Kind, intent.Confidence, intent.Reason, act ? "acting" : "staying quiet");
            if (!act)
            {
                return false;
            }

            switch (intent.Kind)
            {
                case AgentIntentKind.AddToDeck:
                    await PostForDeckAsync(turn, DraftRef.Current, null, announce: true, output, cancellationToken);
                    break;
                case AgentIntentKind.ShiproomUpdate:
                    await ShiproomUpdateAsync(workIq, turn, await ReadAsync(workIq, turn, null, cancellationToken), intent, proactive: true, output, stream: null, cancellationToken);
                    break;
                default:
                    await SummarizeAsync(workIq, turn, await ReadAsync(workIq, turn, null, cancellationToken), null, intent, output, stream: null, cancellationToken);
                    break;
            }

            return true;
        }
        catch (WorkIqException ex)
        {
            // Nobody asked, so there's nobody to tell; the next poll tries again.
            _logger.LogWarning(ex, "Work IQ failed while reading along ({Failure})", ex.Failure);
            return false;
        }
        finally
        {
            state.Gate.Release();
        }
    }

    private async Task AnswerAsync(IWorkIq workIq, AgentTurn turn, Transcript transcript, DirectRequest request, IAgentOutput output, IReplyStream? stream, CancellationToken ct)
    {
        // With a stream, the answer shows as it's written.
        await InformAsync(stream, Messages.CheckingRunbook, ct);
        var (runbook, missingRunbook) = await RunbookAsync(turn, workIq, ct);
        var catalog = runbook.Catalog($"{request.Text}\n{transcript.Render()}", runbookOptions.Value.MaxCatalog);
        var system = Prompts.AnswerSystem(Name, runbooks.FileName, Role);
        var user = Prompts.AnswerUser(request.Text, request.AskerName, transcript, catalog, runbook.Cases.Count, clock.GetUtcNow(), turn.Zone, runbookMissing: missingRunbook is not null);
        IList<AITool> tools = [CaseTool(runbook, stream), MeetingTool(workIq, turn.Zone)];
        var answer = stream is null
            ? await CompleteAsync(system, user, json: false, tools, ct)
            : await StreamAsync(system, user, tools, stream, ct);
        if (stream is { Cancelled: true })
        {
            _logger.LogInformation("The person stopped the answer");
            return;
        }

        if (answer.Length == 0)
        {
            await output.PostAsync(AgentReply.Error(Messages.DraftFailed), ct);
            return;
        }

        var (text, citations) = RunbookCitations.Cite(answer, runbook);
        await output.PostAsync(
            new AgentReply(WithRunbookNote(text, missingRunbook), ReplyKind.Answer)
            {
                Citations = citations,
                Suggestions = [Messages.SuggestSummary(memory.Incident(null)?.Id), Messages.SuggestShiproom],
            },
            ct);
    }

    private async Task SummarizeAsync(IWorkIq workIq, AgentTurn turn, Transcript transcript, string? focus, AgentIntent intent, IAgentOutput output, IReplyStream? stream, CancellationToken ct)
    {
        // Notes this agent wrote earlier: about the named incident (possibly in another conversation), else from here.
        var earlier = (intent.Incident is { } id ? memory.Incident(id) : null)
            ?? memory.For(turn.Key).Summary
            ?? (transcript.IsEmpty ? memory.Incident(null) : null);
        if (transcript.IsEmpty && earlier is null)
        {
            await output.PostAsync(Messages.NothingToSummarize, ct);
            return;
        }

        var note = await WriteSummaryAsync(workIq, turn, transcript, focus, earlier, stream, ct);
        if (note is null)
        {
            await output.PostAsync(AgentReply.Error(Messages.DraftFailed), ct);
            return;
        }

        // The notes keep the plain summary; only the posted copy carries citation markers.
        memory.Remember(turn.Key, note);
        var (runbook, missingRunbook) = await RunbookAsync(turn, workIq, ct);
        var (text, citations) = RunbookCitations.Cite(note.Summary, runbook);
        await output.PostAsync(
            new AgentReply(WithRunbookNote(text, missingRunbook), ReplyKind.Summary) { Citations = citations, Incident = note, Suggestions = [Messages.SuggestShiproom] },
            ct);
    }

    private async Task ShiproomUpdateAsync(IWorkIq workIq, AgentTurn turn, Transcript transcript, AgentIntent intent, bool proactive, IAgentOutput output, IReplyStream? stream, CancellationToken ct)
    {
        var options = shiproomOptions.Value;
        var day = DateResolver.Resolve(intent.When, clock.GetUtcNow(), turn.Zone);
        await InformAsync(stream, Messages.FindingMeeting(intent.Meeting ?? options.MeetingKeyword), ct);
        var meeting = await FindMeetingAsync(workIq, intent.Meeting, day, turn.Zone, ct);
        if (meeting is null)
        {
            await output.PostAsync(Messages.MeetingNotFound(intent.Meeting ?? options.MeetingKeyword, day), ct);
            return;
        }

        var state = memory.For(turn.Key);
        var incident = state.Summary ?? memory.Incident(intent.Incident);

        // Nobody asked, so say what's coming; the draft then replaces this message instead of adding another.
        string? working = null;
        if (proactive)
        {
            working = await output.PostAsync(new AgentReply(Messages.Working(meeting, incident?.Id ?? intent.Incident, turn.Zone), ReplyKind.Status), ct);
        }

        if (incident is null)
        {
            incident = await WriteSummaryAsync(workIq, turn, transcript, null, null, stream, ct);
            if (incident is null)
            {
                await ReplaceAsync(output, working, AgentReply.Error(Messages.DraftFailed), ct);
                return;
            }

            memory.Remember(turn.Key, incident);
        }

        var decks = await deckFinder.FindAsync(workIq, meeting, turn.Zone, ct);
        Slide? template = null;
        if (decks.Prior is { } prior)
        {
            await InformAsync(stream, Messages.ReadingDeck(prior.Name), ct);
            try
            {
                var blob = await workIq.FetchBlobAsync(WorkIqPaths.DriveContent(prior.DriveId, prior.ItemId), ct);
                template = DeckReader.Read(blob.Content, prior.Name).FindSlide(options.SlideTitle);
            }
            catch (Exception ex) when (ex is WorkIqException or InvalidDataException or System.Xml.XmlException)
            {
                _logger.LogWarning(ex, "Couldn't read the previous deck {Deck}", prior.Name);
            }
        }

        var since = transcript.Lines.Where(l => l.Created > incident.At).ToList();
        await InformAsync(stream, Messages.DraftingUpdate(meeting), ct);
        var reply = await CompleteAsync(
            Prompts.ShiproomSystem(Name, Role),
            Prompts.ShiproomUser(meeting, template, template is null ? null : decks.Prior!.Name, incident, since, Name, turn.Zone),
            json: true,
            tools: null,
            ct);
        var (lines, changes) = ParseDraft(reply);
        if (lines.Count == 0)
        {
            await ReplaceAsync(output, working, AgentReply.Error(Messages.DraftFailed), ct);
            return;
        }

        var draft = new ShiproomDraft(meeting, decks.Current, options.SlideTitle, ToDeckLines(lines, template), incident.Id, clock.GetUtcNow())
        {
            Subtitle = Messages.DraftSubtitle(meeting, turn.Zone, Name),
            Footnote = Messages.DraftFootnote(template is null ? null : decks.Prior!.Name, template?.Number, changes),
        };
        if (changes.Count > 0)
        {
            memory.Remember(turn.Key, incident.WithUpdates(changes, clock.GetUtcNow()));
        }

        state.Draft = draft;
        if (await ReplaceAsync(output, working, DraftReply(draft), ct) is { } messageId)
        {
            state.Draft = draft with { MessageId = messageId };
        }
    }

    /// <summary>
    /// Posts the waiting draft as slide text, ready to paste into this week's deck, and marks the draft approved.
    /// <paramref name="announce"/> is false when the request came from the draft itself (a card button or a reaction
    /// on it), where an out-of-date draft needs no reply.
    /// </summary>
    private async Task PostForDeckAsync(AgentTurn turn, DraftRef reference, string? actor, bool announce, IAgentOutput output, CancellationToken ct)
    {
        var state = memory.For(turn.Key);
        if (state.Draft is not { } draft || !draft.Matches(reference) || !draft.CanAct)
        {
            if (announce)
            {
                await output.PostAsync(Messages.NoDraft, ct);
            }

            return;
        }

        state.Draft = null;
        await ShowAsync(output, draft with { Status = DraftStatus.Posted, ActedBy = actor }, ct);
        await output.PostAsync(new AgentReply(Messages.ReadyToPaste(draft), ReplyKind.PasteReady), ct);
        _logger.LogInformation("Posted the update for {Meeting} ready to paste ({Lines} lines)", draft.Meeting.Subject, draft.Lines.Count);
    }

    /// <summary>The draft waiting in a conversation, if any.</summary>
    public ShiproomDraft? Draft(AgentTurn turn) => memory.For(turn.Key).Draft;

    /// <summary>The agent's latest notes on an incident, or on the most recent one.</summary>
    public IncidentNote? Incident(string? id) => memory.Incident(id);

    /// <summary>Someone used the draft's Approve button or approved it with a reaction.</summary>
    public async Task ApproveDraftAsync(AgentTurn turn, DraftRef reference, string? actor, IAgentOutput output, CancellationToken cancellationToken)
    {
        var state = memory.For(turn.Key);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            await PostForDeckAsync(turn, reference, actor, announce: false, output, cancellationToken);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>Drops the draft. Quick, so a card can answer right away; the caller removes the draft's message.</summary>
    public async Task<DraftChange> DiscardDraftAsync(AgentTurn turn, DraftRef reference, string? actor, CancellationToken cancellationToken)
    {
        var state = memory.For(turn.Key);
        if (!await state.Gate.WaitAsync(DraftGateWait, cancellationToken))
        {
            return DraftChange.Failed(Messages.DraftBusy);
        }

        try
        {
            if (state.Draft is not { } draft || !draft.Matches(reference) || !draft.CanAct)
            {
                return DraftChange.Failed(Messages.DraftOutOfDate);
            }

            state.Draft = null;
            _logger.LogInformation("Draft for {Meeting} discarded", draft.Meeting.Subject);
            return new DraftChange(draft with { Status = DraftStatus.Discarded, ActedBy = actor }, null);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    /// <summary>Replaces the draft's lines with someone's edits, keeping the deck's formatting, and updates its message.</summary>
    public async Task<DraftChange> ReviseDraftAsync(AgentTurn turn, DraftRef reference, IReadOnlyList<string> lines, string? actor, IAgentOutput output, CancellationToken cancellationToken)
    {
        var text = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (text.Count == 0)
        {
            return DraftChange.Failed(Messages.DraftEmpty);
        }

        var state = memory.For(turn.Key);
        if (!await state.Gate.WaitAsync(DraftGateWait, cancellationToken))
        {
            return DraftChange.Failed(Messages.DraftBusy);
        }

        try
        {
            if (state.Draft is not { } draft || !draft.Matches(reference) || !draft.CanAct)
            {
                return DraftChange.Failed(Messages.DraftOutOfDate);
            }

            var heading = draft.Lines.FirstOrDefault() ?? new DeckLine(string.Empty, 0, true);
            var detail = draft.Lines.Skip(1).FirstOrDefault() ?? new DeckLine(string.Empty, 1, false);
            var revised = draft with
            {
                Lines = [.. text.Select((t, i) => (i == 0 ? heading : detail) with { Text = t })],
                Status = DraftStatus.Pending,
                ActedBy = actor,
            };
            state.Draft = revised;
            if (!await ShowAsync(output, revised, cancellationToken) && await output.PostAsync(DraftReply(revised), cancellationToken) is { } messageId)
            {
                state.Draft = revised = revised with { MessageId = messageId };
            }

            _logger.LogInformation("Draft for {Meeting} edited ({Lines} lines)", draft.Meeting.Subject, text.Count);
            return new DraftChange(revised, null);
        }
        finally
        {
            state.Gate.Release();
        }
    }

    public static AgentReply DraftReply(ShiproomDraft draft) => new(Messages.Draft(draft), ReplyKind.Draft) { Draft = draft };

    /// <summary>Updates the draft's message in place.</summary>
    private static async Task<bool> ShowAsync(IAgentOutput output, ShiproomDraft draft, CancellationToken ct) =>
        draft.MessageId is { } id && await output.UpdateAsync(id, DraftReply(draft), ct);

    /// <summary>Replaces an earlier message (like "On it") with the reply, or posts the reply when that isn't possible.</summary>
    private static async Task<string?> ReplaceAsync(IAgentOutput output, string? messageId, AgentReply reply, CancellationToken ct) =>
        messageId is not null && await output.UpdateAsync(messageId, reply, ct)
            ? messageId
            : await output.PostAsync(reply, ct);

    /// <summary>
    /// The agent's runbook. When it isn't in the agent user's OneDrive yet, an empty one and its expected path, so
    /// replies that don't need it still go out.
    /// </summary>
    private async Task<(KnowledgeBase Runbook, string? MissingPath)> RunbookAsync(AgentTurn turn, IWorkIq workIq, CancellationToken ct)
    {
        try
        {
            return (await runbooks.GetAsync(turn.SelfId, workIq, ct), null);
        }
        catch (RunbookNotFoundException ex)
        {
            return (KnowledgeBase.Parse(string.Empty, "no runbook"), ex.Path);
        }
    }

    private static string WithRunbookNote(string text, string? missingRunbook) =>
        missingRunbook is null ? text : $"{text}\n\n{Messages.RunbookMissing(missingRunbook)}";

    private async Task<IncidentNote?> WriteSummaryAsync(IWorkIq workIq, AgentTurn turn, Transcript transcript, string? focus, IncidentNote? earlier, IReplyStream? stream, CancellationToken ct)
    {
        await InformAsync(stream, Messages.CheckingRunbook, ct);
        var (runbook, _) = await RunbookAsync(turn, workIq, ct);
        var catalog = runbook.Catalog($"{focus}\n{transcript.Render()}", runbookOptions.Value.MaxCatalog);
        await InformAsync(stream, Messages.Summarizing(transcript), ct);
        var reply = await CompleteAsync(
            Prompts.SummarySystem(Name, Role),
            Prompts.SummaryUser(transcript, focus, earlier, catalog, runbook.Cases.Count, turn.Zone),
            json: true,
            [CaseTool(runbook, stream)],
            ct);

        if (!WorkIqEnvelope.TryParseObject(reply, out var json) || Str(json, "summary") is not { } summary)
        {
            _logger.LogWarning("The model didn't return a summary");
            return null;
        }

        var id = AgentIntent.IncidentId(Str(json, "incident")) ?? earlier?.Id;
        var title = Str(json, "title");
        var text = Messages.WithHeader(Messages.TidySummary(summary), id, title, Str(json, "severity"), Str(json, "status"));
        return new IncidentNote(id, title ?? "Incident", text, clock.GetUtcNow());
    }

    private async Task<AgentIntent> ClassifyAsync(IReadOnlyList<ChatLine> context, IReadOnlyList<ChatLine> newest, bool addressed, bool hasDraft, TimeZoneInfo zone, CancellationToken ct)
    {
        var reply = await CompleteAsync(
            Prompts.IntentSystem(Name, Role),
            Prompts.IntentUser(Name, context, newest, addressed, hasDraft, clock.GetUtcNow(), zone),
            json: true,
            tools: null,
            ct);
        return AgentIntent.Parse(reply);
    }

    private async Task<Meeting?> FindMeetingAsync(IWorkIq workIq, string? name, DateOnly? day, TimeZoneInfo zone, CancellationToken ct)
    {
        var keyword = shiproomOptions.Value.MeetingKeyword;
        var meeting = await calendar.FindAsync(workIq, string.IsNullOrWhiteSpace(name) ? keyword : name, day, zone, ct);

        // The model may pass "tomorrow's shiproom"; fall back to the configured keyword.
        if (meeting is null && !string.IsNullOrWhiteSpace(name) && !name.Equals(keyword, StringComparison.OrdinalIgnoreCase)
            && name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            meeting = await calendar.FindAsync(workIq, keyword, day, zone, ct);
        }

        return meeting;
    }

    private Task<Transcript> ReadAsync(IWorkIq workIq, AgentTurn turn, string? skipMessageId, CancellationToken ct) =>
        turn.Target is ConversationTarget.Unsupported or ConversationTarget.Personal { ChatId: null }
            ? Task.FromResult(new Transcript([], "this conversation", false))
            : reader.ReadAsync(workIq, turn.Target, skipMessageId, turn.SelfId, ct);

    private async Task<string> CompleteAsync(string system, string user, bool json, IList<AITool>? tools, CancellationToken ct)
    {
        var options = llm.Value.ChatOptions();
        if (json)
        {
            options.ResponseFormat = ChatResponseFormat.Json;
        }

        IChatClient client = chat;
        if (tools is { Count: > 0 })
        {
            options.Tools = tools;

            // Not disposed: disposing a delegating client disposes the shared inner client.
            client = new FunctionInvokingChatClient(chat, loggers) { MaximumIterationsPerRequest = MaxToolRounds };
        }

        var response = await client.GetResponseAsync([new(ChatRole.System, system), new(ChatRole.User, user)], options, ct);
        var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.Name).ToList();
        if (calls.Count > 0)
        {
            _logger.LogInformation("Model called {Tools}", string.Join(", ", calls));
        }

        return response.Text.Trim();
    }

    /// <summary>Like <see cref="CompleteAsync"/>, but shows the answer in the stream as the model writes it.</summary>
    private async Task<string> StreamAsync(string system, string user, IList<AITool> tools, IReplyStream stream, CancellationToken ct)
    {
        var options = llm.Value.ChatOptions();
        options.Tools = tools;
        var client = new FunctionInvokingChatClient(chat, loggers) { MaximumIterationsPerRequest = MaxToolRounds };
        var text = new StringBuilder();
        var calls = new List<string>();
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.System, system), new(ChatRole.User, user)], options, ct))
        {
            calls.AddRange(update.Contents.OfType<FunctionCallContent>().Select(c => c.Name));
            if (!string.IsNullOrEmpty(update.Text))
            {
                text.Append(update.Text);
                await stream.AppendAsync(update.Text, ct);
            }

            if (stream.Cancelled)
            {
                break;
            }
        }

        if (calls.Count > 0)
        {
            _logger.LogInformation("Model called {Tools}", string.Join(", ", calls));
        }

        return text.ToString().Trim();
    }

    private static Task InformAsync(IReplyStream? stream, string step, CancellationToken ct) =>
        stream?.InformAsync(step, ct) ?? Task.CompletedTask;

    private static AIFunction CaseTool(KnowledgeBase runbook, IReplyStream? stream = null) => AIFunctionFactory.Create(
        async ([Description("Runbook case IDs from the catalog, e.g. [\"TB-004\"]. At most 3.")] string[] ids, CancellationToken ct) =>
        {
            var wanted = (ids ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxCasesPerCall).ToList();
            if (wanted.Count > 0)
            {
                await InformAsync(stream, Messages.ReadingCases(wanted), ct);
            }

            var sb = new StringBuilder();
            foreach (var id in wanted)
            {
                sb.Append(runbook.Find(id) is { } c ? c.ToMarkdown() : $"There's no case {id}.").Append("\n\n");
            }

            return sb.Length == 0 ? "No case IDs were given." : sb.ToString().TrimEnd();
        },
        CaseToolName,
        "Returns the full cause and fix steps of runbook cases.");

    private AIFunction MeetingTool(IWorkIq workIq, TimeZoneInfo zone) => AIFunctionFactory.Create(
        async (
            [Description("Words from the meeting's subject, e.g. \"shiproom\".")] string name,
            [Description("The day as people said it (\"tomorrow\", \"friday\", \"2026-10-12\"), or empty for the next 7 days.")] string? when,
            CancellationToken ct) =>
        {
            var day = DateResolver.Resolve(when, clock.GetUtcNow(), zone);
            var meeting = await FindMeetingAsync(workIq, name, day, zone, ct);
            return meeting is null ? "No matching meeting on your calendar." : Messages.Describe(meeting, zone);
        },
        MeetingToolName,
        "Finds a meeting on your own calendar (you only see meetings you're invited to).");

    /// <summary>The model's lines, styled like the template: a bold heading line, then indented detail lines.</summary>
    internal static IReadOnlyList<DeckLine> ToDeckLines(IReadOnlyList<string> lines, Slide? template)
    {
        var heading = template?.Lines.FirstOrDefault() ?? new DeckLine(string.Empty, 0, true);
        var detail = template?.Lines.Skip(1).FirstOrDefault() ?? new DeckLine(string.Empty, 1, false);
        return lines.Select((text, i) => i == 0 ? heading with { Text = text } : detail with { Text = text }).ToList();
    }

    internal static (IReadOnlyList<string> Lines, IReadOnlyList<string> Changes) ParseDraft(string reply)
    {
        if (!WorkIqEnvelope.TryParseObject(reply, out var json))
        {
            return ([], []);
        }

        // The template is shown under a "Slide N: title" heading, which the model sometimes copies in as the first line.
        var lines = Strings(json, "lines");
        if (lines.Count > 0 && System.Text.RegularExpressions.Regex.IsMatch(lines[0], @"^Slide \d+\s*:"))
        {
            lines.RemoveAt(0);
        }

        return (lines, Strings(json, "changes"));
    }

    private static List<string> Strings(JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim().Replace("**", string.Empty)).Where(s => s.Length > 0).ToList()
            : [];

    private static string? Str(JsonElement json, string name) =>
        json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim() is { Length: > 0 } s && s != "null" ? s : null;
}
