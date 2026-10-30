using System.Text;
using System.Text.RegularExpressions;

namespace IncidentAgent.Knowledge;

public sealed record KnowledgeCase(string Id, string Category, string Title, string Symptoms, string Keywords, string Cause, string Fix)
{
    /// <summary>One line per case for the model's catalog: enough to pick candidates, not the whole fix.</summary>
    public string CatalogLine => $"{Id} | {Title} | {Symptoms}";

    public string ToMarkdown() =>
        $"### {Id}: {Title}\n**Category:** {Category}\n**Symptoms:** {Symptoms}\n**Keywords:** {Keywords}\n**Cause:** {Cause}\n**Fix:**\n{Fix}";
}

/// <summary>
/// The troubleshooting runbook: a markdown file of <c>### ID: Title</c> blocks with <c>**Symptoms:**</c>,
/// <c>**Keywords:**</c>, <c>**Cause:**</c> and <c>**Fix:**</c> fields, grouped under <c>## Category</c> headings.
/// </summary>
public sealed partial class KnowledgeBase
{
    private readonly Dictionary<string, KnowledgeCase> _byId;
    private readonly List<(KnowledgeCase Case, Dictionary<string, int> Terms, int Length)> _index;
    private readonly Dictionary<string, int> _documentFrequency = new(StringComparer.Ordinal);
    private readonly double _averageLength;

    private KnowledgeBase(IReadOnlyList<KnowledgeCase> cases, string source)
    {
        Cases = cases;
        Source = source;
        _byId = cases.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        _index = [];
        foreach (var c in cases)
        {
            // Title and keywords count twice: they're the most deliberate words in a case.
            var terms = Terms($"{c.Title} {c.Title} {c.Keywords} {c.Keywords} {c.Symptoms} {c.Cause}");
            _index.Add((c, terms, terms.Values.Sum()));
            foreach (var t in terms.Keys)
            {
                _documentFrequency[t] = _documentFrequency.GetValueOrDefault(t) + 1;
            }
        }

        _averageLength = _index.Count == 0 ? 1 : _index.Average(e => e.Length);
    }

    public IReadOnlyList<KnowledgeCase> Cases { get; }

    public string Source { get; }

    public static KnowledgeBase Parse(string markdown, string source = "inline")
    {
        var cases = new List<KnowledgeCase>();
        var category = string.Empty;
        string? id = null, title = null;
        var body = new StringBuilder();

        void Flush()
        {
            if (id is not null)
            {
                cases.Add(Build(id, category, title!, body.ToString(), source));
            }

            id = null;
            body.Clear();
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var heading = CaseHeading().Match(line);
            if (heading.Success)
            {
                Flush();
                id = heading.Groups["id"].Value.ToUpperInvariant();
                title = heading.Groups["title"].Value.Trim();
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                category = line[3..].Trim();
                continue;
            }

            if (id is not null)
            {
                body.Append(line).Append('\n');
            }
        }

        Flush();
        var duplicate = cases.GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new FormatException($"{source}: case {duplicate.Key} appears more than once.");
        }

        return new KnowledgeBase(cases, source);
    }

    public KnowledgeCase? Find(string id) => _byId.GetValueOrDefault(id.Trim());

    /// <summary>BM25 keyword ranking, used to pre-select cases when the playbook is too big to send whole.</summary>
    public IReadOnlyList<(KnowledgeCase Case, double Score)> Search(string text, int top)
    {
        var query = Terms(text).Keys.ToList();
        if (query.Count == 0 || _index.Count == 0)
        {
            return [];
        }

        const double k1 = 1.2, b = 0.75;
        var n = _index.Count;
        return _index
            .Select(e =>
            {
                double score = 0;
                foreach (var t in query)
                {
                    if (!e.Terms.TryGetValue(t, out var tf))
                    {
                        continue;
                    }

                    var df = _documentFrequency[t];
                    var idf = Math.Log(1 + ((n - df + 0.5) / (df + 0.5)));
                    score += idf * (tf * (k1 + 1)) / (tf + (k1 * (1 - b + (b * e.Length / _averageLength))));
                }

                return (e.Case, Score: score);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Case.Id, StringComparer.Ordinal)
            .Take(top)
            .ToList();
    }

    /// <summary>The cases the model sees: all of them, or the best keyword matches when there are more than <paramref name="max"/>.</summary>
    public IReadOnlyList<KnowledgeCase> Catalog(string text, int max) =>
        Cases.Count <= max ? Cases : Search(text, max).Select(r => r.Case).ToList();

    internal static Dictionary<string, int> Terms(string text)
    {
        var terms = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Word().Matches(text.ToLowerInvariant()))
        {
            var w = Stem(m.Value);
            if (w.Length < 2 || StopWords.Contains(w))
            {
                continue;
            }

            terms[w] = terms.GetValueOrDefault(w) + 1;
        }

        return terms;
    }

    private static string Stem(string w)
    {
        if (w.Length > 4 && w.EndsWith("ies", StringComparison.Ordinal))
        {
            return w[..^3] + "y";
        }

        if (w.Length > 3 && w.EndsWith('s') && !w.EndsWith("ss", StringComparison.Ordinal))
        {
            return w[..^1];
        }

        return w;
    }

    private static KnowledgeCase Build(string id, string category, string title, string body, string source)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? current = null;
        var value = new StringBuilder();

        void Save()
        {
            if (current is not null)
            {
                fields[current] = value.ToString().Trim();
            }

            value.Clear();
        }

        foreach (var line in body.Split('\n'))
        {
            var label = FieldLabel().Match(line);
            if (label.Success)
            {
                Save();
                current = label.Groups["label"].Value;
                value.Append(label.Groups["rest"].Value.Trim());
                continue;
            }

            if (current is not null)
            {
                value.Append('\n').Append(line);
            }
        }

        Save();
        foreach (var required in new[] { "Symptoms", "Cause", "Fix" })
        {
            if (!fields.TryGetValue(required, out var v) || v.Length == 0)
            {
                throw new FormatException($"{source}: case {id} has no **{required}:** section.");
            }
        }

        return new KnowledgeCase(id, category, title, fields["Symptoms"], fields.GetValueOrDefault("Keywords") ?? string.Empty, fields["Cause"], fields["Fix"]);
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "that", "this", "from", "but", "not", "are", "was", "were", "you", "your", "our", "its",
        "it", "is", "in", "on", "of", "to", "an", "or", "be", "as", "at", "by", "we", "us", "they", "them", "when", "what",
        "why", "how", "can", "does", "doesn", "don", "did", "has", "have", "had", "will", "would", "should", "could", "just",
        "any", "all", "some", "into", "out", "then", "than", "there", "their", "here", "also", "only", "still", "get", "got",
        "see", "seen", "like", "one", "if", "so", "no", "do", "me", "my", "i", "he", "she", "him", "her", "a",
    };

    [GeneratedRegex(@"^###\s+(?<id>[A-Za-z]{2,6}-\d{1,4})\s*[:·\-–—]\s*(?<title>.+)$")]
    private static partial Regex CaseHeading();

    [GeneratedRegex(@"^\*\*(?<label>Symptoms|Keywords|Cause|Fix):\*\*(?<rest>.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex FieldLabel();

    [GeneratedRegex(@"[a-z0-9][a-z0-9._\-]*[a-z0-9]|[a-z0-9]")]
    private static partial Regex Word();
}
