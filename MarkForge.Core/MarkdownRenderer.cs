using Markdig;

namespace MarkForge.Core;

/// <summary>
/// Converts Markdown source text into HTML using the CommonMark + GFM extension set
/// (tables, task lists, strikethrough, footnotes, auto-links, emoji, etc.).
/// </summary>
public static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseEmojiAndSmiley()
        .UseSoftlineBreakAsHardlineBreak()
        .Use<MermaidExtension>()
        .Use<SourceSyncExtension>()
        .Build();

    /// <summary>Renders markdown to an HTML fragment (no surrounding document).</summary>
    public static string RenderBody(string? markdown)
        => Markdig.Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

    /// <summary>Renders markdown to a complete HTML document for in-app preview — references
    /// the app's WebView2 virtual host for CSS/JS (see <see cref="HtmlTemplate.Wrap"/>), so it
    /// stays well under the 2 MB NavigateToString limit regardless of document size. Used by
    /// both Viewer's normal rendering and Editor's PDF export (which prints the live preview).
    /// For a file meant to be saved and opened outside the app, use
    /// <see cref="RenderStandaloneDocument"/> instead.</summary>
    public static string RenderDocument(
        string? markdown,
        bool darkTheme = false,
        string? fontFamily = null,
        double fontSize = HtmlTemplate.DefaultFontSize,
        int previewWidthPx = HtmlTemplate.DefaultPreviewWidthPx)
        => HtmlTemplate.Wrap(RenderBody(markdown), darkTheme, fontFamily, fontSize, previewWidthPx);

    /// <summary>Renders markdown to a complete standalone HTML document (assets inlined, opens
    /// on its own outside the app — see <see cref="HtmlTemplate.ExportStandalone"/>). Use only
    /// for a file being saved to disk (e.g. the Editor's "匯出為 HTML"); never pass this to
    /// NavigateToString — inlining mermaid.js alone blows past its 2 MB limit.</summary>
    public static string RenderStandaloneDocument(
        string? markdown,
        bool darkTheme = false,
        string? fontFamily = null,
        double fontSize = HtmlTemplate.DefaultFontSize,
        int previewWidthPx = HtmlTemplate.DefaultPreviewWidthPx)
        => HtmlTemplate.ExportStandalone(RenderBody(markdown), darkTheme, fontFamily, fontSize, previewWidthPx);
}
