using System.Text;
using System.Text.RegularExpressions;
using IncidentAgent.Agent;

namespace IncidentAgent.Knowledge;

/// <summary>
/// Turns the runbook case IDs in a reply (like "TB-004") into numbered citations, so people can see where an answer
/// came from without opening the runbook.
/// </summary>
public static partial class RunbookCitations
{
    /// <summary>Teams shows about this much of a citation's abstract.</summary>
    public const int MaxAbstract = 160;

    public const int MaxTitle = 80;

    /// <summary>Teams shows at most three keywords per citation, each up to 28 characters.</summary>
    public const int MaxKeywords = 3;

    public const int MaxKeywordLength = 28;

    /// <summary>
    /// Puts a <c>[n]</c> marker after the first mention of each case the runbook has, numbered in order of appearance.
    /// IDs that aren't in the runbook (incident IDs, say) are left alone.
    /// </summary>
    public static (string Text, IReadOnlyList<Citation> Citations) Cite(string markdown, KnowledgeBase runbook, int max = 10)
    {
        var found = new List<(KnowledgeCase Case, int End)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CaseId().Matches(markdown))
        {
            if (found.Count >= max)
            {
                break;
            }

            if (runbook.Find(m.Value) is { } c && seen.Add(c.Id))
            {
                // Keep the marker outside **bold**, *italics* or `code` around the ID.
                var end = m.Index + m.Length;
                while (end < markdown.Length && markdown[end] is '*' or '_' or '`')
                {
                    end++;
                }

                found.Add((c, end));
            }
        }

        if (found.Count == 0)
        {
            return (markdown, []);
        }

        var text = new StringBuilder(markdown);
        for (var i = found.Count - 1; i >= 0; i--)
        {
            text.Insert(found[i].End, $" [{i + 1}]");
        }

        var citations = found.Select((f, i) => new Citation(
            i + 1,
            Clip($"{f.Case.Id}: {f.Case.Title}", MaxTitle),
            Clip(f.Case.Cause, MaxAbstract),
            [.. f.Case.Keywords.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(k => Clip(k, MaxKeywordLength))
                .Take(MaxKeywords)]))
            .ToList();
        return (text.ToString(), citations);
    }

    private static string Clip(string text, int max)
    {
        var plain = Whitespace().Replace(text.Replace("**", string.Empty).Replace("`", string.Empty), " ").Trim();
        return plain.Length <= max ? plain : plain[..(max - 1)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\b[A-Za-z]{2,6}-\d{1,4}\b")]
    private static partial Regex CaseId();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
