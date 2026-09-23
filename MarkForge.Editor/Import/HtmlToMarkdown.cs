using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using HtmlDocument = HtmlAgilityPack.HtmlDocument;

namespace MarkForge.Editor.Import;

/// <summary>
/// Converts clipboard HTML — primarily what Word, LibreOffice Writer (ODF) and Google Docs
/// put there — into GFM Markdown.
///
/// Headings come from two places: real &lt;h1&gt;–&lt;h6&gt; tags (Word's 標題 1–6 styles,
/// LibreOffice's Heading styles), and plain paragraphs whose font is noticeably bigger than
/// the document's body text (a hand-formatted 24pt title, Word's MsoTitle, Google Docs'
/// Title). The body size is the size carrying the most characters in the pasted content;
/// each paragraph's own size is the size carrying most of <em>its</em> characters, so one
/// big first letter doesn't turn a paragraph into a heading.
///
/// Word's list paragraphs (&lt;p style="mso-list:l0 level2"&gt; with the bullet written out
/// as text inside an &lt;![if !supportLists]&gt; block) are rebuilt into nested Markdown
/// lists; HTML &lt;ul&gt;/&lt;ol&gt; and tables convert as you'd expect.
/// </summary>
internal sealed class HtmlToMarkdown
{
    /// <summary>Body size assumed when the pasted content has only one font size — the usual
    /// Word/Writer body size, so a lone 24pt title still reads as a heading.</summary>
    private const double DefaultBodyPt = 12;

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "h1", "h2", "h3", "h4", "h5", "h6", "ul", "ol", "li", "table", "pre",
        "blockquote", "hr", "section", "article", "header", "footer", "main", "nav", "aside",
        "figure", "figcaption", "dl", "dt", "dd", "address", "center", "body", "html", "form",
        "fieldset", "details", "summary", "tbody", "thead", "tfoot", "tr",
    };

    private static readonly HashSet<string> SkipTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "head", "style", "script", "title", "meta", "link", "noscript", "template", "svg",
        "button", "input", "select", "textarea", "iframe", "object", "xml", "colgroup", "col",
    };

    private static readonly Regex WordListMarkerRegex = new(
        @"<!\[if\s+!supportLists\]>(?<m>.*?)<!\[endif\]>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DownlevelConditionalRegex = new(
        @"<!\[(?:if\s[^\]]*|endif)\]>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MsoListRegex = new(
        @"mso-list\s*:\s*(?<id>l\d+)\s+level(?<level>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OrderedMarkerRegex = new(
        @"^[\(（]?([0-9]+|[a-zA-Z]|[ivxlcdmIVXLCDM]+|[一二三四五六七八九十百]+)[\.\)）、．]", RegexOptions.Compiled);
    private static readonly Regex StyleBlockRegex = new(
        @"<style\b[^>]*>(?<css>.*?)</style>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CssRuleRegex =new(@"(?<sel>[^{}]+)\{(?<body>[^{}]*)\}", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"[ \t\r\n\f\u00A0]+", RegexOptions.Compiled);
    private static readonly Regex LineStartEscapeRegex = new(
        @"^(?<ws>[ ]*)(?:(?<num>\d+)(?<delim>[.)])(?=\s)|(?<sym>#|>|[-+](?=\s)|=+[ ]*$|-+[ ]*$))", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex CjkLineWrapRegex = new(
        @"(?<=[\u2E80-\uFFEF])[ ]*\r?\n[ ]*(?=[\u2E80-\uFFEF])", RegexOptions.Compiled);

    private readonly Dictionary<string, Dictionary<string, string>> _classStyles;
    private readonly Func<string, string?>? _resolveImage;
    private readonly List<Block> _blocks = new();
    private readonly StringBuilder _inline = new();
    private Dictionary<double, int> _inlineSizes = new();
    private bool _inlineHasContent;
    private bool _nested; // converting a list item / quote / cell rather than the whole paste

    private HtmlToMarkdown(Dictionary<string, Dictionary<string, string>> classStyles, Func<string, string?>? resolveImage)
    {
        _classStyles = classStyles;
        _resolveImage = resolveImage;
    }

    /// <param name="html">A full HTML document or a fragment.</param>
    /// <param name="resolveImage">Maps an &lt;img src&gt; (file:///, data:, http…) to the path to
    /// write into the Markdown; return null to keep the original src.</param>
    public static string Convert(string html, Func<string, string?>? resolveImage = null)
    {
        html = WordListMarkerRegex.Replace(html, m =>
        {
            string marker = WebUtility.HtmlDecode(Regex.Replace(m.Groups["m"].Value, "<[^>]+>", string.Empty));
            marker = WhitespaceRegex.Replace(marker, " ").Trim();
            return "<mf-listmarker data-m=\"" + WebUtility.HtmlEncode(marker) + "\"></mf-listmarker>";
        });
        html = DownlevelConditionalRegex.Replace(html, string.Empty);

        var doc = new HtmlDocument { OptionFixNestedTags = true };
        doc.LoadHtml(html);

        var classStyles = ParseStyleSheets(html);
        var converter = new HtmlToMarkdown(classStyles, resolveImage);
        var root = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
        converter.WalkBlocks(root, TextStyle.Default);
        converter.FlushInline(TextStyle.Default);
        converter.PromoteSizedParagraphs();
        return converter.Serialize().Trim('\n') + "\n";
    }

    // ----- Block model -----

    private enum BlockKind { Paragraph, Heading, WordListItem, Raw }

    private sealed class Block
    {
        public BlockKind Kind;
        public string Text = string.Empty;
        public int Level;              // heading level, or Word list depth (0-based)
        public bool Ordered;           // Word list item
        public string ListId = string.Empty; // Word list item: mso-list "l0", "l1"… — a new id starts a new list
        public bool IsList;            // Raw block holding a rendered <ul>/<ol>
        public double? DominantSize;   // pt, paragraphs only
    }

    private readonly record struct TextStyle(double? SizePt, bool Bold, bool Italic, bool Strike, bool Code, bool InPre, bool InHeading)
    {
        public static readonly TextStyle Default = new(null, false, false, false, false, false, false);
    }

    // ----- Walking -----

    private void WalkBlocks(HtmlNode parent, TextStyle style)
    {
        foreach (var node in parent.ChildNodes)
        {
            if (node.NodeType == HtmlNodeType.Comment) continue;
            if (node.NodeType == HtmlNodeType.Text || !BlockTags.Contains(node.Name) || node.Name == "mf-listmarker")
            {
                // An inline element wrapping whole blocks — Google Docs puts everything inside
                // <b style="font-weight:normal" id="docs-internal-guid-…"> — is really a
                // container; walking it as inline would flatten its headings and lists.
                if (node.NodeType == HtmlNodeType.Element && !SkipTags.Contains(node.Name) && HasBlockDescendant(node))
                {
                    WalkBlocks(node, ResolveStyle(node, style));
                    continue;
                }
                AppendInline(node, style);
                continue;
            }

            string tag = node.Name.ToLowerInvariant();
            var childStyle = ResolveStyle(node, style);
            switch (tag)
            {
                case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                    FlushInline(style);
                    string heading = CollapseLines(RenderInlineChildren(node, childStyle with { Bold = true, InHeading = true }));
                    _inlineSizes = new Dictionary<double, int>(); // headings don't count toward body size
                    if (heading.Length > 0)
                        _blocks.Add(new Block { Kind = BlockKind.Heading, Level = tag[1] - '0', Text = heading });
                    break;

                case "p":
                case "dt":
                case "dd":
                case "figcaption":
                case "summary":
                    FlushInline(style);
                    if (TryWordListItem(node, childStyle)) break;
                    WalkBlocks(node, childStyle);
                    FlushInline(childStyle);
                    break;

                case "ul":
                case "ol":
                    FlushInline(style);
                    AddRaw(RenderList(node, childStyle), isList: true);
                    break;

                case "li":
                    // A stray <li> outside any list (partial selection in a browser).
                    FlushInline(style);
                    AddRaw(PrefixLines(ConvertNested(node, childStyle), "- ", "  "));
                    break;

                case "table":
                    FlushInline(style);
                    AddRaw(RenderTable(node, childStyle));
                    break;

                case "pre":
                    FlushInline(style);
                    AddRaw(RenderPre(node));
                    break;

                case "blockquote":
                    FlushInline(style);
                    AddRaw(PrefixLines(ConvertNested(node, childStyle), "> ", "> ", blankPrefix: ">"));
                    break;

                case "hr":
                    FlushInline(style);
                    AddRaw("---");
                    break;

                default: // div, section, body, tr outside tables, …: transparent containers
                    FlushInline(style);
                    WalkBlocks(node, childStyle);
                    FlushInline(childStyle);
                    break;
            }
        }
    }

    private static bool HasBlockDescendant(HtmlNode node)
        => node.Descendants().Any(d => d.NodeType == HtmlNodeType.Element && BlockTags.Contains(d.Name));

    /// <summary>Word writes list items as ordinary paragraphs tagged mso-list, with the
    /// bullet/number rendered as literal text (turned into &lt;mf-listmarker&gt; above).</summary>
    private bool TryWordListItem(HtmlNode p, TextStyle style)
    {
        var levelMatch = MsoListRegex.Match(p.GetAttributeValue("style", string.Empty));
        var markerNode = p.Descendants("mf-listmarker").FirstOrDefault();
        if (!levelMatch.Success && markerNode is null) return false;

        string marker = markerNode is null ? string.Empty : WebUtility.HtmlDecode(markerNode.GetAttributeValue("data-m", string.Empty));
        int level = levelMatch.Success ? Math.Max(0, int.Parse(levelMatch.Groups["level"].Value, CultureInfo.InvariantCulture) - 1) : 0;

        string text = CollapseLines(RenderInlineChildren(p, style)).Trim();
        _inlineSizes = new Dictionary<double, int>();
        _blocks.Add(new Block
        {
            Kind = BlockKind.WordListItem,
            Level = level,
            Ordered = OrderedMarkerRegex.IsMatch(marker),
            ListId = levelMatch.Success ? levelMatch.Groups["id"].Value : string.Empty,
            Text = text,
        });
        return true;
    }

    private void AppendInline(HtmlNode node, TextStyle style)
    {
        string rendered = RenderInline(node, style);
        _inline.Append(rendered);
        if (rendered.Trim().Length > 0) _inlineHasContent = true;
    }

    private void FlushInline(TextStyle style)
    {
        string text = _inline.ToString();
        var sizes = _inlineSizes;
        _inline.Clear();
        _inlineSizes = new Dictionary<double, int>();
        bool hadContent = _inlineHasContent;
        _inlineHasContent = false;
        if (!hadContent) return;

        text = TidyParagraph(text);
        if (text.Length == 0) return;

        double? dominant = sizes.Count == 0 ? null : sizes.MaxBy(kv => kv.Value).Key;
        _blocks.Add(new Block { Kind = BlockKind.Paragraph, Text = text, DominantSize = dominant });
    }

    private void AddRaw(string text, bool isList = false)
    {
        text = text.Trim('\n');
        if (text.Trim().Length > 0) _blocks.Add(new Block { Kind = BlockKind.Raw, Text = text, IsList = isList });
    }

    /// <summary>Converts a container's content (list item, quote, table cell) on its own,
    /// without heading promotion — font-size headings only make sense at the top level.</summary>
    private string ConvertNested(HtmlNode node, TextStyle style)
    {
        var nested = new HtmlToMarkdown(_classStyles, _resolveImage) { _nested = true };
        nested.WalkBlocks(node, style);
        nested.FlushInline(style);
        return nested.Serialize().Trim('\n');
    }

    // ----- Inline rendering -----

    private string RenderInlineChildren(HtmlNode node, TextStyle style)
    {
        var sb = new StringBuilder();
        foreach (var child in node.ChildNodes) sb.Append(RenderInline(child, style));
        return sb.ToString();
    }

    private string RenderInline(HtmlNode node, TextStyle parent)
    {
        if (node.NodeType == HtmlNodeType.Comment) return string.Empty;
        if (node.NodeType == HtmlNodeType.Text)
        {
            string raw = WebUtility.HtmlDecode(((HtmlTextNode)node).Text);
            if (parent.InPre) return raw;
            // Word hard-wraps its HTML source; between CJK characters that newline must vanish
            // rather than turn into a space.
            string text = WhitespaceRegex.Replace(CjkLineWrapRegex.Replace(raw, string.Empty), " ");
            int visible = text.Count(ch => !char.IsWhiteSpace(ch));
            if (visible > 0 && parent.SizePt is double size)
                _inlineSizes[size] = _inlineSizes.GetValueOrDefault(size) + visible;
            return parent.Code ? text : EscapeInline(text);
        }

        string tag = node.Name.ToLowerInvariant();
        if (SkipTags.Contains(tag) || tag == "mf-listmarker") return string.Empty;
        if (tag.Contains(':') && tag != "o:p") return string.Empty; // VML/Office namespaced shapes

        var style = ResolveStyle(node, parent);
        switch (tag)
        {
            case "br":
                return parent.InHeading ? " " : "\n";
            case "img":
                return RenderImage(node);
            case "table":
                // Table nested inside inline context (e.g. a <td> inside a <span>): flatten.
                return " " + WhitespaceRegex.Replace(WebUtility.HtmlDecode(node.InnerText), " ").Trim() + " ";
        }

        if (BlockTags.Contains(tag))
        {
            // Block inside inline flow (a <p> inside <a>, a <div> inside a <td>…): keep the
            // separation as a line break.
            string inner = RenderInlineChildren(node, style).Trim();
            return inner.Length == 0 ? string.Empty : "\n" + inner + "\n";
        }

        string content;
        if (style.Code && !parent.Code)
        {
            string code = WhitespaceRegex.Replace(WebUtility.HtmlDecode(node.InnerText), " ");
            if (code.Trim().Length == 0) return code;
            string fence = code.Contains('`') ? "`` " : "`";
            string closing = code.Contains('`') ? " ``" : "`";
            return fence + code.Trim() + closing;
        }

        content = RenderInlineChildren(node, style);

        if (tag == "a")
        {
            string href = node.GetAttributeValue("href", string.Empty).Trim();
            if (href.Length > 0 && !href.StartsWith('#') && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
                && content.Trim().Length > 0)
            {
                string label = content.Trim();
                return (content.StartsWith(' ') ? " " : string.Empty) + "[" + label + "](" + EncodeUrl(href) + ")"
                     + (content.EndsWith(' ') ? " " : string.Empty);
            }
            return content;
        }

        if (style.Strike && !parent.Strike) content = Wrap(content, "~~");
        if (style.Italic && !parent.Italic) content = Wrap(content, "*");
        if (style.Bold && !parent.Bold) content = Wrap(content, "**");
        if (tag is "sup" or "sub" && content.Trim().Length > 0) content = "<" + tag + ">" + content.Trim() + "</" + tag + ">";
        return content;
    }

    private string RenderImage(HtmlNode img)
    {
        string src = WebUtility.HtmlDecode(img.GetAttributeValue("src", string.Empty)).Trim();
        if (src.Length == 0) return string.Empty;
        string alt = WhitespaceRegex.Replace(WebUtility.HtmlDecode(img.GetAttributeValue("alt", string.Empty)), " ").Trim();
        alt = alt.Replace("[", "\\[").Replace("]", "\\]");
        string path = _resolveImage?.Invoke(src) ?? src;
        return "![" + alt + "](" + EncodeUrl(path) + ")";
    }

    private static string Wrap(string content, string marker)
    {
        string trimmed = content.Trim();
        if (trimmed.Length == 0) return content;
        int lead = content.Length - content.TrimStart().Length;
        int trail = content.Length - content.TrimEnd().Length;
        // Emphasis can't span a hard line break in Markdown — wrap each line separately.
        var lines = trimmed.Split('\n').Select(l => l.Trim().Length == 0 ? l : marker + l.Trim() + marker);
        return content[..lead] + string.Join("\n", lines) + content[(content.Length - trail)..];
    }

    // ----- Lists / tables / code -----

    private string RenderList(HtmlNode list, TextStyle style)
    {
        bool ordered = list.Name.Equals("ol", StringComparison.OrdinalIgnoreCase);
        int number = list.GetAttributeValue("start", 1);
        var items = new List<string>();

        foreach (var child in list.ChildNodes)
        {
            if (child.NodeType != HtmlNodeType.Element) continue;
            string body;
            if (child.Name.Equals("li", StringComparison.OrdinalIgnoreCase))
                body = ConvertNested(child, ResolveStyle(child, style));
            else if (child.Name is "ul" or "ol")
            {
                // Sloppy nesting (<ul><li>a</li><ul>…</ul></ul>): attach to the previous item.
                string nested = RenderList(child, style);
                if (items.Count > 0)
                {
                    string indent = new string(' ', ordered ? (number - 1).ToString(CultureInfo.InvariantCulture).Length + 2 : 2);
                    items[^1] += "\n" + PrefixLines(nested, indent, indent);
                }
                continue;
            }
            else continue;

            string marker = ordered ? number.ToString(CultureInfo.InvariantCulture) + ". " : "- ";
            number++;
            items.Add(PrefixLines(body.Length == 0 ? string.Empty : body, marker, new string(' ', marker.Length)));
        }
        return string.Join("\n", items);
    }

    private string RenderTable(HtmlNode table, TextStyle style)
    {
        var rows = new List<IReadOnlyList<string>>();
        foreach (var tr in table.Descendants("tr"))
        {
            // Only this table's rows, not rows of a table nested in one of its cells.
            if (tr.Ancestors("table").FirstOrDefault() != table) continue;

            var cells = new List<string>();
            foreach (var cell in tr.ChildNodes.Where(n => n.Name is "td" or "th"))
            {
                // MarkdownTable escapes pipes and turns the remaining newlines into <br>.
                string text = ConvertNested(cell, ResolveStyle(cell, style));
                cells.Add(Regex.Replace(text, @"\n{2,}", "\n"));
                int span = Math.Clamp(cell.GetAttributeValue("colspan", 1), 1, 50);
                for (int i = 1; i < span; i++) cells.Add(string.Empty);
            }
            rows.Add(cells);
        }
        return MarkdownTable.Build(rows) ?? string.Empty;
    }

    private static string RenderPre(HtmlNode pre)
    {
        var sb = new StringBuilder();
        void Collect(HtmlNode n)
        {
            foreach (var c in n.ChildNodes)
            {
                if (c.NodeType == HtmlNodeType.Text) sb.Append(WebUtility.HtmlDecode(((HtmlTextNode)c).Text));
                else if (c.Name == "br") sb.Append('\n');
                else if (c.NodeType == HtmlNodeType.Element) Collect(c);
            }
        }
        Collect(pre);

        string code = sb.ToString().Replace("\r\n", "\n").Trim('\n');
        string language = string.Empty;
        var codeEl = pre.Descendants("code").FirstOrDefault();
        foreach (string cls in (codeEl?.GetAttributeValue("class", string.Empty) + " " + pre.GetAttributeValue("class", string.Empty)).Split(' '))
        {
            if (cls.StartsWith("language-", StringComparison.Ordinal)) { language = cls["language-".Length..]; break; }
            if (cls.StartsWith("lang-", StringComparison.Ordinal)) { language = cls["lang-".Length..]; break; }
        }
        string fence = code.Contains("```") ? "~~~~" : "```";
        return fence + language + "\n" + code + "\n" + fence;
    }

    // ----- Font-size headings -----

    private void PromoteSizedParagraphs()
    {
        // Paragraphs without an explicit size (LibreOffice leaves body text at the style
        // default) count too, under -1: if they're the majority, the body is DefaultBodyPt.
        var histogram = new Dictionary<double, int>();
        foreach (var b in _blocks.Where(b => b.Kind == BlockKind.Paragraph))
        {
            double key = b.DominantSize ?? -1;
            histogram[key] = histogram.GetValueOrDefault(key) + b.Text.Length;
        }
        if (!histogram.Keys.Any(k => k > 0)) return;

        double body = histogram.Count == 1 ? DefaultBodyPt : histogram.MaxBy(kv => kv.Value).Key;
        if (body <= 0) body = DefaultBodyPt;
        foreach (var b in _blocks)
        {
            if (b.Kind != BlockKind.Paragraph || b.DominantSize is not double size) continue;
            if (b.Text.Length > 120 || b.Text.Contains('\n') || b.Text.StartsWith("![", StringComparison.Ordinal)) continue;

            double ratio = size / body;
            int level = ratio >= 1.75 ? 1 : ratio >= 1.45 ? 2 : ratio >= 1.25 ? 3 : ratio >= 1.12 ? 4 : 0;
            if (level == 0) continue;

            b.Kind = BlockKind.Heading;
            b.Level = level;
            b.Text = b.Text.Replace("**", string.Empty).Trim();
        }
    }

    // ----- Output -----

    private string Serialize()
    {
        var sb = new StringBuilder();
        var counters = new int[10];
        var indents = new string[10];
        Block? previous = null;

        foreach (var b in _blocks)
        {
            // Consecutive items of the same Word list stay together; inside a list item, a
            // sublist hugs the item's text (a blank line there would make the list "loose").
            bool tight = (previous?.Kind == BlockKind.WordListItem && b.Kind == BlockKind.WordListItem && previous.ListId == b.ListId)
                         || (_nested && b.IsList && previous?.Kind == BlockKind.Paragraph);
            if (sb.Length > 0) sb.Append(tight ? "\n" : "\n\n");

            switch (b.Kind)
            {
                case BlockKind.Heading:
                    sb.Append(new string('#', Math.Clamp(b.Level, 1, 6))).Append(' ').Append(b.Text);
                    break;

                case BlockKind.WordListItem:
                {
                    int level = Math.Min(b.Level, counters.Length - 1);
                    if (previous?.Kind != BlockKind.WordListItem || previous.ListId != b.ListId) Array.Clear(counters);
                    for (int i = level + 1; i < counters.Length; i++) counters[i] = 0;
                    counters[level]++;

                    string indent = string.Empty;
                    for (int i = 0; i < level; i++) indent += indents[i] ?? "  ";
                    string marker = b.Ordered ? counters[level].ToString(CultureInfo.InvariantCulture) + ". " : "- ";
                    indents[level] = new string(' ', marker.Length);
                    sb.Append(indent).Append(marker).Append(b.Text);
                    break;
                }

                default:
                    sb.Append(b.Text);
                    break;
            }
            previous = b;
        }
        return sb.ToString();
    }

    private static string TidyParagraph(string text)
    {
        var lines = text.Replace("\r", string.Empty).Split('\n').Select(l => l.Trim()).ToList();
        // Drop blank lines at the edges and squeeze runs of them — Word loves empty spans.
        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var result = new List<string>();
        foreach (string line in lines)
        {
            if (line.Length == 0 && result.Count > 0 && result[^1].Length == 0) continue;
            result.Add(line);
        }

        string joined = string.Join("\n", result);
        // Word splits one bold run into several adjacent <b> elements: **a****b** → **ab**
        joined = joined.Replace("****", string.Empty).Replace("~~~~", string.Empty);
        return LineStartEscapeRegex.Replace(joined, m => m.Groups["num"].Success
            ? m.Groups["ws"].Value + m.Groups["num"].Value + "\\" + m.Groups["delim"].Value
            : m.Groups["ws"].Value + "\\" + m.Groups["sym"].Value);
    }

    private static string CollapseLines(string text)
        => TidyParagraph(text).Replace("\n", " ").Trim();

    private static string PrefixLines(string text, string first, string rest, string? blankPrefix = null)
    {
        var lines = text.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.Append('\n');
            if (lines[i].Length == 0 && i > 0) { sb.Append(blankPrefix ?? string.Empty); continue; }
            sb.Append(i == 0 ? first : rest).Append(lines[i]);
        }
        return sb.ToString();
    }

    private static string EscapeInline(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            switch (ch)
            {
                case '*':
                case '`':
                case '[':
                case ']':
                case '\\' when i + 1 < text.Length && char.IsPunctuation(text[i + 1]):
                    sb.Append('\\').Append(ch);
                    break;
                case '_' when (i == 0 || !char.IsLetterOrDigit(text[i - 1])) || (i + 1 == text.Length || !char.IsLetterOrDigit(text[i + 1])):
                    sb.Append("\\_");
                    break;
                case '<' when i + 1 < text.Length && (char.IsLetter(text[i + 1]) || text[i + 1] is '/' or '!'):
                    sb.Append("&lt;");
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string EncodeUrl(string url)
    {
        url = url.Trim();
        return url.IndexOfAny(new[] { ' ', '(', ')', '<', '>' }) >= 0 ? "<" + url.Replace("<", "%3C").Replace(">", "%3E") + ">" : url;
    }

    // ----- CSS -----

    private TextStyle ResolveStyle(HtmlNode node, TextStyle parent)
    {
        var style = parent;
        string tag = node.Name.ToLowerInvariant();

        switch (tag)
        {
            case "b": case "strong": style = style with { Bold = true }; break;
            case "i": case "em": case "cite": case "var": style = style with { Italic = true }; break;
            case "s": case "strike": case "del": style = style with { Strike = true }; break;
            case "code": case "kbd": case "samp": case "tt": style = style with { Code = true }; break;
            case "th": style = style with { Bold = true }; break;
            case "pre": style = style with { InPre = true }; break;
            case "font":
            {
                string size = node.GetAttributeValue("size", string.Empty);
                if (int.TryParse(size, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int n))
                {
                    if (size.StartsWith('+') || size.StartsWith('-')) n += 3;
                    double[] map = { 7.5, 7.5, 10, 12, 13.5, 18, 24, 36 };
                    style = style with { SizePt = map[Math.Clamp(n, 1, 7)] };
                }
                break;
            }
        }

        // Class rules from the document's <style> first, inline style attribute last (wins).
        foreach (string cls in node.GetAttributeValue("class", string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_classStyles.TryGetValue(tag + "." + cls.ToLowerInvariant(), out var tagRule)) style = ApplyDeclarations(style, parent, tagRule);
            else if (_classStyles.TryGetValue("." + cls.ToLowerInvariant(), out var rule)) style = ApplyDeclarations(style, parent, rule);
        }

        string inline = node.GetAttributeValue("style", string.Empty);
        if (inline.Length > 0) style = ApplyDeclarations(style, parent, ParseDeclarations(WebUtility.HtmlDecode(inline)));
        return style;
    }

    private static TextStyle ApplyDeclarations(TextStyle style, TextStyle parent, Dictionary<string, string> decls)
    {
        if (decls.TryGetValue("font-size", out string? fontSize) && ParseFontSize(fontSize, parent.SizePt) is double pt)
            style = style with { SizePt = pt };

        if (decls.TryGetValue("font-weight", out string? weight))
        {
            weight = weight.Trim().ToLowerInvariant();
            if (weight is "bold" or "bolder") style = style with { Bold = true };
            else if (int.TryParse(weight, out int w)) style = style with { Bold = w >= 600 };
            else if (weight is "normal" or "lighter") style = style with { Bold = false };
        }

        if (decls.TryGetValue("font-style", out string? fontStyle))
        {
            fontStyle = fontStyle.Trim().ToLowerInvariant();
            if (fontStyle is "italic" or "oblique") style = style with { Italic = true };
            else if (fontStyle == "normal") style = style with { Italic = false };
        }

        string decoration = (decls.GetValueOrDefault("text-decoration") ?? string.Empty) + " " + (decls.GetValueOrDefault("text-decoration-line") ?? string.Empty);
        if (decoration.Contains("line-through", StringComparison.OrdinalIgnoreCase)) style = style with { Strike = true };

        return style;
    }

    private static double? ParseFontSize(string value, double? parentPt)
    {
        value = value.Trim().ToLowerInvariant();
        var m = Regex.Match(value, @"^(?<n>[0-9]*\.?[0-9]+)\s*(?<u>pt|px|em|rem|%)?");
        if (!m.Success) return null;
        double n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
        double? pt = m.Groups["u"].Value switch
        {
            "pt" or "" => n,
            "px" => n * 0.75,
            "em" or "rem" => (parentPt ?? DefaultBodyPt) * n,
            "%" => (parentPt ?? DefaultBodyPt) * n / 100,
            _ => null,
        };
        // Half-point granularity, so 10.5pt and a px value converting to 10.4999pt compare equal.
        return pt is double v ? Math.Round(v * 2, MidpointRounding.AwayFromZero) / 2 : null;
    }

    private static Dictionary<string, string> ParseDeclarations(string css)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in css.Split(';'))
        {
            int colon = part.IndexOf(':');
            if (colon <= 0) continue;
            result[part[..colon].Trim()] = part[(colon + 1)..].Trim().Trim('"', '\'');
        }
        return result;
    }

    /// <summary>Class-based rules from &lt;style&gt; blocks, keyed "tag.class" or ".class".
    /// Word defines MsoTitle / heading sizes there instead of inline.</summary>
    /// <remarks>Read straight from the raw HTML rather than the parsed DOM: on the clipboard,
    /// Word puts its stylesheet inside a conditional comment (&lt;!--[if gte mso 10]&gt;
    /// &lt;style&gt;…), so a parser never sees a &lt;style&gt; element at all.</remarks>
    private static Dictionary<string, Dictionary<string, string>> ParseStyleSheets(string html)
    {
        var rules = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (Match block in StyleBlockRegex.Matches(html))
        {
            string css = Regex.Replace(block.Groups["css"].Value, @"/\*.*?\*/|<!--|-->", string.Empty, RegexOptions.Singleline);
            css = Regex.Replace(css, @"@[a-z-]+[^{;]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}|@[^;{]+;", string.Empty, RegexOptions.IgnoreCase);
            foreach (Match m in CssRuleRegex.Matches(css))
            {
                var decls = ParseDeclarations(m.Groups["body"].Value);
                foreach (string rawSel in m.Groups["sel"].Value.Split(','))
                {
                    string sel = rawSel.Trim().ToLowerInvariant();
                    if (!Regex.IsMatch(sel, @"^[a-z0-9]*\.[a-z0-9_-]+$")) continue;
                    if (!rules.TryGetValue(sel, out var existing)) rules[sel] = existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in decls) existing[kv.Key] = kv.Value;
                }
            }
        }
        return rules;
    }
}

/// <summary>Helpers for the Windows "HTML Format" (CF_HTML) clipboard payload.</summary>
internal static class ClipboardHtml
{
    /// <summary>
    /// Strips the CF_HTML header (Version / StartHTML / … / SourceURL lines) and returns the
    /// HTML document after it. The header's offsets are deliberately ignored: they count
    /// UTF-8 bytes, but Word writes parts of its &lt;head&gt; (CSS font names) in the ANSI
    /// code page, so once the payload has been decoded to a string they no longer line up —
    /// trusting them cut the stylesheet off and lost Word's Title/body sizes.
    /// </summary>
    public static string Extract(string cfHtml)
    {
        if (!cfHtml.StartsWith("Version:", StringComparison.OrdinalIgnoreCase)) return cfHtml;

        var firstMarkupLine = Regex.Match(cfHtml, "^[ \t]*<", RegexOptions.Multiline);
        return firstMarkupLine.Success ? cfHtml[firstMarkupLine.Index..] : cfHtml;
    }

    /// <summary>Office suites and word processors whose HTML is worth converting to Markdown
    /// structure, as opposed to e.g. a code editor's syntax-coloured spans.</summary>
    public static bool IsFromDocumentApp(string html)
    {
        return Regex.IsMatch(html,
            @"urn:schemas-microsoft-com:office|content=""?(Microsoft Word|Microsoft Excel|LibreOffice|OpenOffice|WPS)|docs-internal-guid|<meta[^>]+generator[^>]+(LibreOffice|OpenOffice)",
            RegexOptions.IgnoreCase);
    }

    /// <summary>True when the HTML carries structure Markdown can express (headings, lists,
    /// tables, emphasis, images, links) and isn't a monospace code snippet.</summary>
    public static bool HasMarkdownWorthyStructure(string html)
    {
        if (Regex.IsMatch(html, @"white-space\s*:\s*pre", RegexOptions.IgnoreCase)) return false;
        return Regex.IsMatch(html, @"<(h[1-6]|table|ul|ol|li|b|strong|em|i|img|a\s|blockquote|pre)\b", RegexOptions.IgnoreCase);
    }
}
