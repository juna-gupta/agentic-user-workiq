using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentAgent.Agent;

namespace IncidentAgent.Teams;

/// <summary>
/// The meeting draft as an Adaptive Card. "Approve" and "Discard" are Universal Actions (Action.Execute), so the
/// card updates in place for everyone; "Edit" opens a dialog (task module) with the lines as text boxes. Each action
/// carries the draft's stamp, so pressing a button on an older card doesn't act on a newer draft.
/// </summary>
public static class DraftCards
{
    public const string ApproveVerb = "approveDraft";
    public const string DiscardVerb = "discardDraft";
    public const string EditAction = "editDraft";
    public const string SaveAction = "saveDraft";
    public const string DraftKey = "draft";
    public const string ActionKey = "action";
    public const string LinePrefix = "line";

    /// <summary>The dialog shows the draft's lines plus a couple of empty ones for additions.</summary>
    public const int SpareLines = 2;

    /// <summary>The notification text for the card: its title and the slide heading.</summary>
    public static string Summary(ShiproomDraft draft) =>
        draft.Lines.FirstOrDefault()?.Text is { Length: > 0 } heading ? $"{draft.Title}: {heading}" : draft.Title;

    public static JsonObject Card(ShiproomDraft draft)
    {
        var details = string.Join("\n", draft.Lines.Skip(1).Select(l => "- " + Messages.BoldLabel(l.Text)));
        var lines = new JsonArray(TextBlock(draft.Lines.FirstOrDefault()?.Text ?? string.Empty, ("weight", "Bolder")));
        if (details.Length > 0)
        {
            lines.Add(TextBlock(details));
        }

        var body = new JsonArray
        {
            TextBlock(draft.Title, ("weight", "Bolder"), ("size", "Medium")),
            TextBlock(draft.Subtitle, ("isSubtle", true), ("spacing", "None"), ("size", "Small")),
            new JsonObject { ["type"] = "Container", ["style"] = "emphasis", ["spacing"] = "Medium", ["items"] = lines },
            TextBlock(draft.Footnote, ("isSubtle", true), ("size", "Small")),
            TextBlock(Messages.DraftState(draft), ("color", draft.Status switch
            {
                DraftStatus.Posted => "Good",
                _ => "Default",
            })),
        };

        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["msteams"] = new JsonObject { ["width"] = "Full" },
            ["body"] = body,
            ["actions"] = Actions(draft),
        };
    }

    public static JsonObject EditForm(ShiproomDraft draft)
    {
        var body = new JsonArray
        {
            TextBlock("The first line is the heading. Clear a line to remove it.", ("isSubtle", true)),
        };
        var count = draft.Lines.Count + SpareLines;
        for (var i = 0; i < count; i++)
        {
            body.Add(new JsonObject
            {
                ["type"] = "Input.Text",
                ["id"] = LinePrefix + i,
                ["label"] = i == 0 ? "Heading" : $"Line {i + 1}",
                ["value"] = i < draft.Lines.Count ? draft.Lines[i].Text : string.Empty,
                ["isMultiline"] = i > 0,
                ["maxLength"] = 400,
            });
        }

        return new JsonObject
        {
            ["type"] = "AdaptiveCard",
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["version"] = "1.5",
            ["body"] = body,
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "Action.Submit",
                    ["title"] = "Save",
                    ["style"] = "positive",
                    ["data"] = new JsonObject { [ActionKey] = SaveAction, [DraftKey] = draft.Stamp },
                },
            },
        };
    }

    /// <summary>The edited lines from a submitted <see cref="EditForm"/>, in order, empty ones included.</summary>
    public static IReadOnlyList<string> ReadEditForm(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return data.EnumerateObject()
            .Where(p => p.Name.StartsWith(LinePrefix, StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.String)
            .Select(p => (Index: int.TryParse(p.Name.AsSpan(LinePrefix.Length), out var n) ? n : -1, Text: p.Value.GetString()!))
            .Where(p => p.Index >= 0)
            .OrderBy(p => p.Index)
            .Select(p => p.Text)
            .ToList();
    }

    public static string? Str(JsonElement data, string key) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Reads a string from an Action.Execute's data, whose values arrive as JSON elements.</summary>
    public static string? Str(IReadOnlyDictionary<string, object>? data, string key) =>
        data is not null && data.TryGetValue(key, out var v)
            ? v switch
            {
                JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
                string s => s,
                _ => null,
            }
            : null;

    /// <summary>A task module request's data, which the SDK hands over as an untyped object.</summary>
    public static JsonElement AsJson(object? data) => data switch
    {
        JsonElement e => e,
        null => default,
        _ => JsonSerializer.SerializeToElement(data),
    };

    private static JsonArray Actions(ShiproomDraft draft)
    {
        var actions = new JsonArray();
        if (draft.CanAct)
        {
            var data = new JsonObject { [DraftKey] = draft.Stamp };
            actions.Add(new JsonObject
            {
                ["type"] = "Action.Execute",
                ["title"] = "Approve",
                ["verb"] = ApproveVerb,
                ["style"] = "positive",
                ["data"] = data.DeepClone(),
            });

            actions.Add(new JsonObject
            {
                ["type"] = "Action.Submit",
                ["title"] = "Edit",
                ["data"] = new JsonObject
                {
                    ["msteams"] = new JsonObject { ["type"] = "task/fetch" },
                    [ActionKey] = EditAction,
                    [DraftKey] = draft.Stamp,
                },
            });
            actions.Add(new JsonObject
            {
                ["type"] = "Action.Execute",
                ["title"] = "Discard",
                ["verb"] = DiscardVerb,
                ["style"] = "destructive",
                ["data"] = data.DeepClone(),
            });
        }
        else if (draft is { Status: DraftStatus.Posted, Deck.WebUrl: { } url })
        {
            actions.Add(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open deck", ["url"] = url });
        }

        return actions;
    }

    private static JsonObject TextBlock(string text, params (string Key, JsonNode? Value)[] extra)
    {
        var block = new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true };
        foreach (var (key, value) in extra)
        {
            block[key] = value;
        }

        return block;
    }
}
