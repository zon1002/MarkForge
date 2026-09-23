using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MarkForge.Core;

/// <summary>
/// Tags every heading with a <c>data-src-line</c> attribute holding its zero-based source
/// line number. The preview's scroll-sync script (see <see cref="HtmlTemplate"/>) uses these,
/// together with the code-block tagging <see cref="MermaidExtension"/> already does, as
/// "landmarks" mapping source lines to rendered pixel positions — far more accurate than a
/// single document-wide scroll ratio, which drifts badly once headings, prose, and a tall
/// Mermaid diagram or code block are mixed together (the source and the rendered output
/// simply don't grow at the same rate).
/// </summary>
public sealed class SourceSyncExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.DocumentProcessed += OnDocumentProcessed;
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        // Headings need no renderer changes — Markdig's own HeadingRenderer already writes
        // out any HtmlAttributes attached to the block, straight onto the <hN> tag.
    }

    private static void OnDocumentProcessed(MarkdownDocument document)
    {
        foreach (HeadingBlock heading in document.Descendants<HeadingBlock>())
        {
            heading.GetAttributes().AddPropertyIfNotExist("data-src-line", heading.Line.ToString());
        }
    }
}
