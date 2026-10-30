using System.Globalization;
using System.Text.Json;
using IncidentAgent.Text;

namespace IncidentAgent.Conversations;

/// <summary>
/// Turns chat message JSON from Work IQ into transcript lines, dropping system events and deleted messages. The agent
/// user is a member like anyone else, so its own posts and the messages that @mention it are recognised by its user ID.
/// </summary>
public static class ChatMessageParser
{
    /// <summary>Reads any user message, flagging the agent's own posts and messages that mention it.</summary>
    public static bool TryRead(JsonElement message, string? selfId, out ChatLine line)
    {
        line = null!;
        var id = Str(message, "id") ?? string.Empty;
        if (Str(message, "messageType") is { } type && !string.Equals(type, "message", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (message.TryGetProperty("deletedDateTime", out var deleted) && deleted.ValueKind == JsonValueKind.String)
        {
            return false;
        }

        var from = Obj(message, "from");
        var user = Obj(from, "user");
        var app = Obj(from, "application");
        var authorId = (user is { } u0 ? Str(u0, "id") : null) ?? (app is { } a0 ? Str(a0, "id") : null);
        var author = (user is { } u ? Str(u, "displayName") : null) ?? (app is { } ap ? Str(ap, "displayName") : null) ?? "Someone";
        var body = Obj(message, "body");
        var text = body is { } b ? HtmlText.ToPlain(Str(b, "content"), Str(b, "contentType")) : string.Empty;

        var prefix = new List<string>();
        if (Str(message, "subject") is { Length: > 0 } subject)
        {
            prefix.Add($"Subject: {subject}");
        }

        var extras = new List<string>();
        if (message.TryGetProperty("attachments", out var attachments) && attachments.ValueKind == JsonValueKind.Array)
        {
            foreach (var att in attachments.EnumerateArray())
            {
                var contentType = Str(att, "contentType") ?? string.Empty;
                if (contentType == "messageReference")
                {
                    if (Quote(att) is { } quote)
                    {
                        prefix.Add(quote);
                    }
                }
                else if (contentType.StartsWith("application/vnd.microsoft.card", StringComparison.OrdinalIgnoreCase))
                {
                    extras.Add("[card]");
                }
                else if (Str(att, "name") is { Length: > 0 } name)
                {
                    extras.Add($"[file: {name}]");
                }
            }
        }

        var full = string.Join("\n", prefix.Append(text).Concat(extras).Where(s => s.Length > 0)).Trim();
        if (full.Length == 0 || !TryDate(message, "createdDateTime", out var created))
        {
            return false;
        }

        line = new ChatLine(id, author, created, full)
        {
            AuthorId = authorId,
            FromSelf = selfId is not null && string.Equals(authorId, selfId, StringComparison.OrdinalIgnoreCase),
            MentionsSelf = selfId is not null && Mentions(message, selfId),
        };
        return true;
    }

    /// <summary>Reads a message for a transcript: the agent's own posts and the requests sent to it aren't part of the conversation.</summary>
    public static bool TryParse(JsonElement message, string? skipMessageId, string? selfId, out ChatLine line)
    {
        if (!TryRead(message, selfId, out line) || line.FromSelf || line.MentionsSelf || (skipMessageId is not null && line.Id == skipMessageId))
        {
            line = null!;
            return false;
        }

        return true;
    }

    public static bool TryDate(JsonElement message, string property, out DateTimeOffset value)
    {
        value = default;
        return Str(message, property) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }

    private static string? Quote(JsonElement attachment)
    {
        if (Str(attachment, "content") is not { } json)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var preview = Str(doc.RootElement, "messagePreview");
            var sender = Obj(Obj(doc.RootElement, "messageSender"), "user") is { } s ? Str(s, "displayName") : null;
            return preview is null ? null : $"(quoting {sender ?? "a message"}: \"{HtmlText.Truncate(preview, 120)}\")";
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Mentions(JsonElement message, string selfId) =>
        message.TryGetProperty("mentions", out var mentions) && mentions.ValueKind == JsonValueKind.Array
        && mentions.EnumerateArray().Any(m =>
            Obj(m, "mentioned") is { } who
            && ((Obj(who, "user") is { } u && string.Equals(Str(u, "id"), selfId, StringComparison.OrdinalIgnoreCase))
                || (Obj(who, "application") is { } a && string.Equals(Str(a, "id"), selfId, StringComparison.OrdinalIgnoreCase))));

    private static JsonElement? Obj(JsonElement? parent, string name) =>
        parent is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
