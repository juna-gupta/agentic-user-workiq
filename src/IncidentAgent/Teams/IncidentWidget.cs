#pragma warning disable ExperimentalTeamsHtmlWidget

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentAgent.Agent;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.HtmlWidget;

namespace IncidentAgent.Teams;

/// <summary>
/// A small status tile under an incident summary, sent as a Teams HTML widget (preview). It shows the incident's
/// severity, status and latest update, and its Refresh button calls the <see cref="ToolName"/> tool on the agent,
/// which answers from the agent's current notes.
/// </summary>
public static partial class IncidentWidget
{
    public const string ToolName = "incident_status";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static MessageActivityInput Message(string markdown, IncidentNote incident, string domain) =>
        HtmlWidgetHelpers.BuildHtmlWidgetMessage(
            new HtmlWidgetPayload
            {
                Name = "incident-status",
                Description = $"Status of {incident.Id ?? incident.Title}",
                Html = Html(incident),
                Domain = domain,
                ToolInput = new { id = incident.Id },
                ToolOutput = JsonSerializer.SerializeToElement(Status(incident), Json),
            },
            new HtmlWidgetMarkdownOptions { Before = Blocks(markdown) });

    /// <summary>
    /// Puts a blank line between the summary's lines. A widget message is extended markdown, where a single line break
    /// joins lines into one paragraph and a list needs a blank line before it. List items stay together, and code
    /// blocks are left alone.
    /// </summary>
    internal static string Blocks(string markdown)
    {
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');
        var output = new System.Text.StringBuilder(markdown.Length + lines.Length);
        var inCode = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i > 0)
            {
                var previous = lines[i - 1];
                var bothText = previous.Trim().Length > 0 && line.Trim().Length > 0;
                var sameList = ListItem().IsMatch(previous) && (ListItem().IsMatch(line) || line.StartsWith(' '))
                    || ListItem().IsMatch(line) && previous.StartsWith(' ');
                output.Append(bothText && !inCode && !sameList && !line.TrimStart().StartsWith("```", StringComparison.Ordinal) ? "\n\n" : "\n");
            }

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inCode = !inCode;
            }

            output.Append(line);
        }

        return output.ToString();
    }

    /// <summary>The tool's answer: the incident as the agent knows it now, as JSON text.</summary>
    public static string ToolResult(IncidentNote incident) => JsonSerializer.Serialize(Status(incident), Json);

    /// <summary>The incident ID the widget asked about, from the tool call's arguments.</summary>
    public static string? RequestedId(object? arguments) =>
        DraftCards.Str(DraftCards.AsJson(arguments), "id");

    public static IncidentStatus Status(IncidentNote incident)
    {
        var header = Header().Match(incident.Summary);
        var severity = header.Success && header.Groups["sev"].Success ? header.Groups["sev"].Value.Trim() : null;
        var status = header.Success && header.Groups["status"].Success ? header.Groups["status"].Value.Trim() : null;
        return new IncidentStatus(
            incident.Id,
            incident.Title,
            severity,
            status,
            incident.At.UtcDateTime.ToString("MMM d, h:mm tt 'UTC'", CultureInfo.InvariantCulture),
            incident.Updates.Count > 0 ? incident.Updates[^1] : null);
    }

    private static string Html(IncidentNote incident)
    {
        var s = Status(incident);
        string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
        var tone = s.Severity is { } sev && Regex.IsMatch(sev, @"\b[0-2]\b") ? "high" : "normal";
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8"><style>
            body{font-family:"Segoe UI",system-ui,sans-serif;margin:0;padding:12px;color:#242424;font-size:14px}
            .row{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
            .pill{border-radius:10px;padding:2px 8px;font-size:12px;font-weight:600;background:#ebebeb}
            .pill.high{background:#fde7e9;color:#a4262c}
            .muted{color:#616161;font-size:12px}
            #latest{margin:8px 0 0}
            button{margin-top:10px;border:1px solid #d1d1d1;background:#fff;border-radius:4px;padding:4px 12px;cursor:pointer}
            @media (prefers-color-scheme:dark){body{color:#fff}.pill{background:#3d3d3d}.muted{color:#adadad}button{background:#292929;color:#fff;border-color:#666} }
            </style></head><body>
            <div class="row"><strong id="title">{{E(s.Id is null ? s.Title : $"{s.Id}: {s.Title}")}}</strong>
            <span id="sev" class="pill {{tone}}">{{E(s.Severity ?? "No severity")}}</span>
            <span id="status" class="pill">{{E(s.Status ?? "Status unknown")}}</span></div>
            <p id="latest">{{E(s.Latest ?? "No updates since the summary.")}}</p>
            <div class="muted">As of <span id="updated">{{E(s.Updated)}}</span></div>
            <button id="refresh" type="button">Refresh</button>
            <script>
            (function(){
              var pending = {};
              window.addEventListener('message', function(e){
                var d = e.data;
                if(!d || d.jsonrpc !== '2.0' || !pending[d.id]) return;
                pending[d.id](d); delete pending[d.id];
              });
              function set(id, text){ document.getElementById(id).textContent = text; }
              document.getElementById('refresh').addEventListener('click', function(){
                var id = 'call-' + Math.random().toString(36).slice(2);
                var button = document.getElementById('refresh');
                button.disabled = true; button.textContent = 'Refreshing';
                pending[id] = function(d){
                  button.disabled = false; button.textContent = 'Refresh';
                  try {
                    var s = JSON.parse(d.result.content[0].text);
                    set('sev', s.severity || 'No severity');
                    set('status', s.status || 'Status unknown');
                    set('latest', s.latest || 'No updates since the summary.');
                    set('updated', s.updated);
                  } catch (err) { set('latest', 'Couldn\'t refresh. Ask me in the chat instead.'); }
                };
                window.parent.postMessage({ jsonrpc: '2.0', id: id, method: 'tools/call',
                  params: { name: '{{ToolName}}', arguments: { id: {{JsonSerializer.Serialize(s.Id)}} } } }, '*');
              });
            })();
            </script>
            </body></html>
            """;
    }

    /// <summary>"<c>**ID · title (Sev N) — status**</c>", the line every summary opens with.</summary>
    [GeneratedRegex(@"^\*\*[^\n]*?(?:\((?<sev>[^)\n]+)\))?(?:\s+—\s+(?<status>[^*\n]+))?\*\*", RegexOptions.ExplicitCapture)]
    private static partial Regex Header();

    [GeneratedRegex(@"^\s*(?:[-*+]|\d+[.)])\s")]
    private static partial Regex ListItem();
}

public sealed record IncidentStatus(string? Id, string Title, string? Severity, string? Status, string Updated, string? Latest);
