using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace MarkForge.Core;

/// <summary>
/// Markdig extension that renders fenced code blocks tagged <c>```mermaid</c> as
/// <c>&lt;pre class="mermaid"&gt;...&lt;/pre&gt;</c> instead of a syntax-highlighted code
/// block, so the Mermaid.js runtime (loaded separately — see <see cref="HtmlTemplate"/>)
/// can find and render them as diagrams. Every other fenced/indented code block is left
/// untouched and still goes through highlight.js as before.
/// </summary>
public sealed class MermaidExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        // Nothing to configure on the parser side; the fenced-code-block parser Markdig
        // already ships with is fine as-is — we only need to change how it's rendered.
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer htmlRenderer) return;

        for (int i = 0; i < htmlRenderer.ObjectRenderers.Count; i++)
        {
            if (htmlRenderer.ObjectRenderers[i] is CodeBlockRenderer)
            {
                htmlRenderer.ObjectRenderers[i] = new MermaidAwareCodeBlockRenderer();
                return;
            }
        }
    }

    private sealed class MermaidAwareCodeBlockRenderer : CodeBlockRenderer
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock obj)
        {
            // Approximate — off by the fence lines themselves, which .Lines doesn't include —
            // but this only feeds the scroll-sync "atomic block" pin zone (see HtmlTemplate),
            // where being a line or two off doesn't matter.
            int endLine = obj.Line + obj.Lines.Count + 2;

            if (obj is FencedCodeBlock { Info: "mermaid" })
            {
                renderer.Write("<pre class=\"mermaid\" data-src-line=\"" + obj.Line + "\" data-src-end=\"" + endLine + "\">");
                renderer.WriteEscape(obj.Lines.ToString());
                renderer.Write("</pre>\n");
                return;
            }

            obj.GetAttributes().AddPropertyIfNotExist("data-src-line", obj.Line.ToString());
            obj.GetAttributes().AddPropertyIfNotExist("data-src-end", endLine.ToString());
            base.Write(renderer, obj);
        }
    }
}
