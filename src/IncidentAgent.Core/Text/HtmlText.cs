using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace IncidentAgent.Text;

/// <summary>Converts Teams message HTML (a chat message's <c>body</c>) to plain text for prompts.</summary>
public static partial class HtmlText
{
    public static string ToPlain(string? content, string? contentType = "html")
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        if (!string.Equals(contentType, "html", StringComparison.OrdinalIgnoreCase))
        {
            return Tidy(content);
        }

        var s = content;
        s = Mention().Replace(s, m => "@" + StripTags(m.Groups["name"].Value));
        s = Emoji().Replace(s, m => m.Groups["alt"].Value);
        s = Attachment().Replace(s, string.Empty);
        s = Image().Replace(s, "[image]");
        s = Code().Replace(s, m => "`" + StripTags(m.Groups["code"].Value) + "`");
        s = LineBreak().Replace(s, "\n");
        s = BlockEnd().Replace(s, "\n");
        s = ListItem().Replace(s, "\n- ");
        s = StripTags(s);
        return Tidy(WebUtility.HtmlDecode(s));
    }

    /// <summary>Shortens text to <paramref name="max"/> characters on a word boundary.</summary>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', Math.Max(0, max - 1), Math.Min(max, 40));
        return (cut > 0 ? text[..cut] : text[..max]).TrimEnd() + "…";
    }

    /// <summary>The https links in a message or meeting invite, in order and without duplicates.</summary>
    public static IReadOnlyList<string> Links(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        return Href().Matches(html)
            .Select(m => WebUtility.HtmlDecode(m.Groups["url"].Value).Trim())
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string StripTags(string s) => Tag().Replace(s, string.Empty);

    private static string Tidy(string s)
    {
        var sb = new StringBuilder(s.Length);
        var blank = 0;
        foreach (var raw in s.Replace("\r\n", "\n").Replace('\u00A0', ' ').Split('\n'))
        {
            var line = Spaces().Replace(raw, " ").Trim();
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(blank > 0 ? "\n\n" : "\n");
            }

            sb.Append(line);
            blank = 0;
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"<at\b[^>]*>(?<name>.*?)</at>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Mention();

    [GeneratedRegex(@"<emoji\b[^>]*\balt=""(?<alt>[^""]*)""[^>]*>(?:</emoji>)?", RegexOptions.IgnoreCase)]
    private static partial Regex Emoji();

    [GeneratedRegex(@"<attachment\b[^>]*>(?:</attachment>)?", RegexOptions.IgnoreCase)]
    private static partial Regex Attachment();

    [GeneratedRegex(@"<img\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Image();

    [GeneratedRegex(@"<code\b[^>]*>(?<code>.*?)</code>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Code();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"</(?:p|div|h[1-6]|tr|blockquote|pre|ul|ol)>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<a\b[^>]*?\bhref\s*=\s*[""'](?<url>[^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex Href();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex Spaces();
}
