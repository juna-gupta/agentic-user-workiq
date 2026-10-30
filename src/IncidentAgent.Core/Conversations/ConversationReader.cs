using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using IncidentAgent.WorkIq;

namespace IncidentAgent.Conversations;

/// <summary>Reads conversation messages through Work IQ as the agent user.</summary>
public sealed class ConversationReader(IOptions<ConversationOptions> options, TimeProvider clock, ILogger<ConversationReader> logger)
{
    private ConversationOptions Options => options.Value;

    /// <param name="skipMessageId">The request message itself, which isn't part of the conversation.</param>
    /// <param name="selfId">The agent user's ID, so its own posts and the requests sent to it are left out.</param>
    public Task<Transcript> ReadAsync(IWorkIq workIq, ConversationTarget target, string? skipMessageId, string? selfId, CancellationToken cancellationToken) =>
        target switch
        {
            ConversationTarget.ChannelThread thread => ReadThreadAsync(workIq, thread, skipMessageId, selfId, cancellationToken),
            ConversationTarget.GroupChat chat => ReadChatAsync(workIq, chat.ChatId, skipMessageId, selfId, cancellationToken),
            ConversationTarget.Personal { ChatId: { } id } => ReadChatAsync(workIq, id, skipMessageId, selfId, cancellationToken),
            _ => throw new ArgumentException($"Can't read messages for {target.GetType().Name}.", nameof(target)),
        };

    /// <summary>
    /// One page of the newest messages, oldest first, including the agent's own posts and mentions (flagged on each line).
    /// This is what the watcher polls, so it's a single Work IQ call.
    /// </summary>
    public async Task<IReadOnlyList<ChatLine>> ReadRecentAsync(IWorkIq workIq, ConversationTarget target, int top, string? selfId, CancellationToken cancellationToken)
    {
        var path = target switch
        {
            ConversationTarget.GroupChat chat => WorkIqPaths.ChatMessages(chat.ChatId, top),
            ConversationTarget.Personal { ChatId: { } id } => WorkIqPaths.ChatMessages(id, top),
            ConversationTarget.ChannelThread t => WorkIqPaths.ChannelReplies(t.TeamId, t.ChannelId, t.RootMessageId, top),
            _ => throw new ArgumentException($"Can't read messages for {target.GetType().Name}.", nameof(target)),
        };

        var page = (await workIq.FetchAsync(path, cancellationToken)).EnsureSuccess();
        var lines = new List<ChatLine>();
        foreach (var item in page.Items)
        {
            if (ChatMessageParser.TryRead(item, selfId, out var line))
            {
                lines.Add(line);
            }
        }

        return lines.OrderBy(l => l.Created).ToList();
    }

    /// <summary>Channel: the post and all of its replies (up to <see cref="ConversationOptions.MaxMessages"/>, newest kept).</summary>
    private async Task<Transcript> ReadThreadAsync(IWorkIq workIq, ConversationTarget.ChannelThread thread, string? skip, string? selfId, CancellationToken ct)
    {
        var first = await workIq.FetchAsync(
            [
                WorkIqPaths.ChannelMessage(thread.TeamId, thread.ChannelId, thread.RootMessageId),
                WorkIqPaths.ChannelReplies(thread.TeamId, thread.ChannelId, thread.RootMessageId, Options.PageSize),
            ],
            ct);

        var rootResult = first[0].EnsureSuccess();
        ChatLine? root = ChatMessageParser.TryParse(rootResult.Data!.Value, skip, selfId, out var r) ? r with { IsRoot = true } : null;

        var replies = new List<ChatLine>();
        var page = first[1].EnsureSuccess();
        var truncated = false;
        while (true)
        {
            foreach (var item in page.Items)
            {
                if (ChatMessageParser.TryParse(item, skip, selfId, out var line))
                {
                    replies.Add(line);
                }
            }

            if (page.NextPath is not { } next)
            {
                break;
            }

            if (replies.Count >= Options.MaxMessages)
            {
                truncated = true;
                break;
            }

            try
            {
                page = (await workIq.FetchAsync(next, ct)).EnsureSuccess();
            }
            catch (WorkIqException ex) when (ex.Failure == WorkIqFailure.BadRequest)
            {
                // If the next page of replies can't be read, keep the replies already read.
                truncated = true;
                break;
            }
        }

        // Replies may come back in any order; keep the newest when there are too many.
        var kept = replies.OrderByDescending(l => l.Created).Take(Options.MaxMessages).OrderBy(l => l.Created).ToList();
        truncated |= kept.Count < replies.Count;

        var lines = new List<ChatLine>();
        if (root is not null)
        {
            lines.Add(root);
        }

        lines.AddRange(kept);
        var description = kept.Count switch
        {
            0 => "this post",
            1 => "this post and its reply",
            _ => truncated ? $"this post and its latest {kept.Count} replies" : $"this post and its {kept.Count} replies",
        };

        return Fit(lines, description, truncated);
    }

    /// <summary>Group chat: every message from the last <see cref="ConversationOptions.GroupChatHours"/> hours or the last
    /// <see cref="ConversationOptions.GroupChatMinimumMessages"/> messages, whichever is more.</summary>
    private async Task<Transcript> ReadChatAsync(IWorkIq workIq, string chatId, string? skip, string? selfId, CancellationToken ct)
    {
        var cutoff = clock.GetUtcNow().AddHours(-Options.GroupChatHours);
        var minimum = Options.GroupChatMinimumMessages;
        var newestFirst = new List<ChatLine>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var path = WorkIqPaths.ChatMessages(chatId, Options.PageSize);
        DateTimeOffset? floor = null;
        var hitCap = false;
        var pages = 0;

        // Pages come most recently modified first. Everything not read yet was modified, and so created, before `floor`.
        while (true)
        {
            var page = (await workIq.FetchAsync(path, ct)).EnsureSuccess();
            pages++;
            var keptBefore = newestFirst.Count;
            DateTimeOffset? pageFloor = null;
            foreach (var item in page.Items)
            {
                if (ChatMessageParser.TryDate(item, "lastModifiedDateTime", out var modified) || ChatMessageParser.TryDate(item, "createdDateTime", out modified))
                {
                    pageFloor = pageFloor is { } o && o <= modified ? o : modified;
                }

                if (ChatMessageParser.TryParse(item, skip, selfId, out var line) && seen.Add(line.Id))
                {
                    newestFirst.Add(line);
                }
            }

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(
                    "Chat page {Page}: {Items} items ({Types}), {Kept} kept, modified back to {Floor:o}, next page: {More}",
                    pages, page.Items.Count(), DescribeTypes(page.Items), newestFirst.Count - keptBefore, pageFloor, page.NextPath is not null);
            }

            var progressed = pageFloor is { } pf && (floor is null || pf < floor);
            if (progressed)
            {
                floor = pageFloor;
            }

            if (newestFirst.Count >= Options.MaxMessages)
            {
                hitCap = floor is null || floor >= cutoff;
                break;
            }

            // A nextLink means there's more, so ask for messages modified before the oldest one seen. Stop once nothing
            // unread could be in the window or among the newest `minimum` messages, or if a page didn't move further back.
            if (page.NextPath is null || !progressed)
            {
                break;
            }

            if (floor < cutoff && newestFirst.Count(l => l.Created >= floor) >= minimum)
            {
                break;
            }

            path = WorkIqPaths.ChatMessages(chatId, Options.PageSize, floor);
        }

        newestFirst = [.. newestFirst.OrderByDescending(l => l.Created)];
        var recent = newestFirst.TakeWhile(l => l.Created >= cutoff).Take(Options.MaxMessages).ToList();
        List<ChatLine> chosen;
        string description;
        if (recent.Count >= minimum)
        {
            chosen = recent;
            description = hitCap
                ? $"the latest {chosen.Count} messages from the last {Options.GroupChatHours} hours"
                : $"the last {Options.GroupChatHours} hours ({chosen.Count} messages)";
        }
        else
        {
            chosen = newestFirst.Take(minimum).ToList();
            description = chosen.Count < minimum ? $"all {chosen.Count} messages in this chat" : $"the last {chosen.Count} messages";
        }

        chosen.Reverse();
        logger.LogDebug("Chat read: {Total} messages over {Pages} pages, {Recent} from the last {Hours} hours, summarizing {Chosen}",
            newestFirst.Count, pages, recent.Count, Options.GroupChatHours, chosen.Count);
        return Fit(chosen, description, hitCap);
    }

    private static string DescribeTypes(IEnumerable<JsonElement> items) =>
        string.Join(", ", items
            .GroupBy(i => i.ValueKind == JsonValueKind.Object && i.TryGetProperty("messageType", out var t) ? t.ToString() : "?")
            .Select(g => $"{g.Key}={g.Count()}"));

    /// <summary>Keeps the transcript within the prompt budget by dropping the oldest replies (never a thread's root post).</summary>
    private Transcript Fit(List<ChatLine> lines, string description, bool truncated)
    {
        var size = lines.Sum(Cost);
        var dropped = 0;
        while (size > Options.MaxTranscriptChars && lines.Count > 1)
        {
            var index = lines[0].IsRoot ? 1 : 0;
            if (index >= lines.Count)
            {
                break;
            }

            size -= Cost(lines[index]);
            lines.RemoveAt(index);
            dropped++;
        }

        if (dropped > 0)
        {
            description += $", leaving out the oldest {dropped} to fit";
            truncated = true;
        }

        return new Transcript(lines, description, truncated);
    }

    private static int Cost(ChatLine line) => line.Author.Length + line.Text.Length + 32;
}
