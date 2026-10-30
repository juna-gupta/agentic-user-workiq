using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace IncidentAgent.Shiproom;

/// <summary>One paragraph of slide text with its outline level and whether it's bold (a block's heading line).</summary>
public sealed record DeckLine(string Text, int Level = 0, bool Bold = false);

public sealed record Slide(int Number, string Title, IReadOnlyList<DeckLine> Lines, string? Notes);

public sealed record Deck(string Name, IReadOnlyList<Slide> Slides)
{
    public Slide? FindSlide(string title) => Slides.FirstOrDefault(s => s.Title.Contains(title, StringComparison.OrdinalIgnoreCase));

    public static string Render(Slide slide)
    {
        var sb = new StringBuilder();
        sb.Append("Slide ").Append(slide.Number).Append(": ").Append(slide.Title.Length > 0 ? slide.Title : "(untitled)").Append('\n');
        foreach (var line in slide.Lines)
        {
            sb.Append(new string(' ', 2 * line.Level)).Append(line.Text).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(slide.Notes))
        {
            sb.Append("Speaker notes: ").Append(slide.Notes).Append('\n');
        }

        return sb.ToString().TrimEnd();
    }
}

/// <summary>Reads the text of a .pptx (slides in presentation order, tables as <c>a | b | c</c> rows, speaker notes).</summary>
public static class DeckReader
{
    internal static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    internal static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    internal static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";

    private const int MaxSlides = 200;
    private const long MaxPartBytes = 8 * 1024 * 1024;

    public static Deck Read(byte[] pptx, string name)
    {
        using var zip = new ZipArchive(new MemoryStream(pptx, writable: false), ZipArchiveMode.Read);
        var slides = new List<Slide>();
        var number = 0;
        foreach (var part in SlideParts(zip))
        {
            var doc = Load(zip, part);
            if (doc is null)
            {
                continue;
            }

            number++;
            var (title, lines) = ReadShapes(doc);
            slides.Add(new Slide(number, title, lines, ReadNotes(zip, part)));
        }

        return new Deck(name, slides);
    }

    /// <summary>Slide part names in presentation order (<c>ppt/slides/slide1.xml</c>, …).</summary>
    internal static IReadOnlyList<string> SlideParts(ZipArchive zip)
    {
        var presentation = Load(zip, "ppt/presentation.xml") ?? throw new InvalidDataException("This isn't a PowerPoint file.");
        var rels = Relationships(zip, "ppt/presentation.xml");
        return (presentation.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? [])
            .Select(id => (string?)id.Attribute(R + "id"))
            .Select(rid => rid is not null && rels.TryGetValue(rid, out var r) ? Resolve("ppt/presentation.xml", r.Target) : null)
            .OfType<string>()
            .Take(MaxSlides)
            .ToList();
    }

    internal static XDocument? Load(ZipArchive zip, string part)
    {
        var entry = zip.GetEntry(part);
        if (entry is null)
        {
            return null;
        }

        if (entry.Length > MaxPartBytes)
        {
            throw new InvalidDataException($"{part} is too large to read.");
        }

        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    internal static string Text(XElement paragraph) => string.Concat(paragraph.Descendants(A + "t").Select(t => t.Value)).Trim();

    internal static XElement? Placeholder(XElement shape) => shape.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");

    internal static bool IsTitle(XElement shape) => (string?)Placeholder(shape)?.Attribute("type") is "title" or "ctrTitle";

    private static (string Title, IReadOnlyList<DeckLine> Lines) ReadShapes(XDocument slide)
    {
        var title = string.Empty;
        var lines = new List<DeckLine>();
        var tree = slide.Root?.Element(P + "cSld")?.Element(P + "spTree");
        if (tree is null)
        {
            return (title, lines);
        }

        foreach (var el in tree.Descendants().Where(e => e.Name == P + "sp" || e.Name == A + "tbl"))
        {
            if (el.Name == A + "tbl")
            {
                foreach (var row in el.Elements(A + "tr"))
                {
                    var cells = row.Elements(A + "tc").Select(tc => string.Join(" ", tc.Descendants(A + "p").Select(Text).Where(t => t.Length > 0)));
                    lines.Add(new DeckLine(string.Join(" | ", cells)));
                }

                continue;
            }

            var paragraphs = el.Element(P + "txBody")?.Elements(A + "p") ?? [];
            if (IsTitle(el))
            {
                title = string.Join(" ", paragraphs.Select(Text).Where(t => t.Length > 0));
                continue;
            }

            foreach (var p in paragraphs)
            {
                var text = Text(p);
                if (text.Length == 0)
                {
                    continue;
                }

                var level = (int?)p.Element(A + "pPr")?.Attribute("lvl") ?? 0;
                var bold = p.Descendants().Any(e => (e.Name == A + "rPr" || e.Name == A + "defRPr") && (string?)e.Attribute("b") is "1" or "true");
                lines.Add(new DeckLine(text, level, bold));
            }
        }

        return (title, lines);
    }

    private static string? ReadNotes(ZipArchive zip, string slidePart)
    {
        var notes = Relationships(zip, slidePart).Values.FirstOrDefault(r => r.Type.EndsWith("/notesSlide", StringComparison.Ordinal));
        if (notes.Target is null || Load(zip, Resolve(slidePart, notes.Target)) is not { } doc)
        {
            return null;
        }

        var body = doc.Descendants(P + "sp")
            .Where(sp => (string?)Placeholder(sp)?.Attribute("type") == "body")
            .SelectMany(sp => sp.Descendants(A + "p").Select(Text))
            .Where(t => t.Length > 0);
        var text = string.Join(" ", body).Trim();
        return text.Length == 0 ? null : text;
    }

    private static Dictionary<string, (string Type, string Target)> Relationships(ZipArchive zip, string part)
    {
        var slash = part.LastIndexOf('/');
        var relsPart = $"{part[..(slash + 1)]}_rels/{part[(slash + 1)..]}.rels";
        var doc = Load(zip, relsPart);
        return (doc?.Root?.Elements(Rel + "Relationship") ?? [])
            .Where(r => (string?)r.Attribute("TargetMode") != "External")
            .Select(r => (Id: (string?)r.Attribute("Id"), Type: (string?)r.Attribute("Type") ?? string.Empty, Target: (string?)r.Attribute("Target")))
            .Where(r => r.Id is not null && r.Target is not null)
            .GroupBy(r => r.Id!)
            .ToDictionary(g => g.Key, g => (g.First().Type, g.First().Target!));
    }

    /// <summary>Resolves a relationship target relative to the part that owns it.</summary>
    internal static string Resolve(string ownerPart, string target)
    {
        if (target.StartsWith('/'))
        {
            return target.TrimStart('/');
        }

        var segments = ownerPart.Split('/').SkipLast(1).ToList();
        foreach (var segment in target.Split('/'))
        {
            if (segment == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }
            }
            else if (segment is not ("." or ""))
            {
                segments.Add(segment);
            }
        }

        return string.Join('/', segments);
    }
}
