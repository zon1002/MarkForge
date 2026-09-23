using System.Globalization;
using System.Text.Json;

namespace MarkForge.Core;

/// <summary>
/// Builds the HTML shell used to display rendered Markdown inside a WebView2 control.
/// The GitHub-style CSS, highlight.js and mermaid.js are embedded resources baked into
/// this assembly, so previewing works fully offline — nothing is fetched from a CDN at
/// runtime. They're extracted to disk and served through a WebView2 virtual host rather
/// than inlined into the page: mermaid.min.js alone is well over the 2 MB size limit that
/// WebView2's NavigateToString enforces, so inlining it made every preview fail to load.
/// </summary>
public static class HtmlTemplate
{
    public const string AssetsHostName = "markforge.assets";
    public const string AssetsOrigin = "https://markforge.assets/";

    /// <summary>
    /// Host the preview page loads the document's own relative images from. Pages are shown
    /// via NavigateToString (no base URL), so a relative &lt;img src&gt; would never resolve;
    /// the page rewrites them to https://markforge.doc/&lt;path&gt; and the app serves that
    /// host — the Editor through WebResourceRequested (so not-yet-saved dragged/pasted images
    /// work too), the Viewer by mapping the host to the .md file's folder.
    /// </summary>
    public const string DocumentHostName = "markforge.doc";

    /// <summary>Path prefix under <see cref="DocumentHostName"/> for images referenced by an
    /// absolute Windows path (C:/pics/a.png or file:///C:/pics/a.png).</summary>
    public const string AbsolutePathPrefix = "__abs__/";

    private static readonly string[] AssetFileNames =
    {
        "highlight.min.js",
        "mermaid.min.js",
        "github.min.css",
        "github-dark.min.css",
    };

    public const string DefaultFontFamily = "Segoe UI";
    public const double DefaultFontSize = 16;
    public const int DefaultPreviewWidthPx = 860;

    /// <summary>
    /// A full HTML document with an empty content container plus an
    /// <c>updateContent(html)</c> JS hook, used for live-updating previews so the
    /// page never has to fully reload (scroll position is preserved) while typing.
    /// </summary>
    public static string Shell(bool darkTheme, string? fontFamily = null, double fontSize = DefaultFontSize, int previewWidthPx = DefaultPreviewWidthPx)
        => Build(bodyHtml: string.Empty, darkTheme, live: true, fontFamily, fontSize, previewWidthPx: previewWidthPx);

    /// <summary>A full HTML document, referencing the app's WebView2 virtual host for its CSS/JS
    /// assets — only renders correctly from inside a MarkForge WebView2 instance.</summary>
    public static string Wrap(string bodyHtml, bool darkTheme, string? fontFamily = null, double fontSize = DefaultFontSize, int previewWidthPx = DefaultPreviewWidthPx)
        => Build(bodyHtml, darkTheme, live: false, fontFamily, fontSize, previewWidthPx: previewWidthPx);

    /// <summary>
    /// A full, self-contained HTML document with the content, CSS and JS assets all inlined —
    /// unlike <see cref="Wrap"/>, this opens correctly on its own in any browser. Use this for
    /// anything meant to be saved and opened outside the app (the Editor's "匯出為 HTML"
    /// feature). Inlining is safe here because this never goes through WebView2's
    /// NavigateToString — it's written straight to a file — so the 2 MB size limit that method
    /// enforces (see <see cref="Wrap"/>'s remarks and MermaidExtension) doesn't apply.
    /// </summary>
    public static string ExportStandalone(string bodyHtml, bool darkTheme, string? fontFamily = null, double fontSize = DefaultFontSize, int previewWidthPx = DefaultPreviewWidthPx)
        => Build(bodyHtml, darkTheme, live: false, fontFamily, fontSize, inline: true, previewWidthPx: previewWidthPx);

    public static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".avif" => "image/avif",
        ".tif" or ".tiff" => "image/tiff",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        ".mp3" => "audio/mpeg",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream",
    };

    /// <summary>JSON-encodes a string for safe injection into a script call.</summary>
    public static string ToJsString(string value) => JsonSerializer.Serialize(value);

    /// <summary>
    /// Extracts the embedded CSS/JS assets to a per-app folder under %TEMP% and returns that
    /// folder's path, ready to be passed to
    /// <c>CoreWebView2.SetVirtualHostNameToFolderMapping(AssetsHostName, folder, ...)</c>.
    /// Files are only rewritten when missing or a different size, so repeat calls are cheap.
    /// </summary>
    public static string EnsureAssetsExtracted(string appName)
    {
        string folder = Path.Combine(Path.GetTempPath(), "MarkForge", appName, "WebAssets");
        Directory.CreateDirectory(folder);

        var assembly = typeof(HtmlTemplate).Assembly;
        foreach (string fileName in AssetFileNames)
        {
            string resourceName = $"{assembly.GetName().Name}.Assets.{fileName}";
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");

            string destPath = Path.Combine(folder, fileName);
            if (File.Exists(destPath) && new FileInfo(destPath).Length == stream.Length) continue;

            using var fileStream = File.Create(destPath);
            stream.CopyTo(fileStream);
        }

        return folder;
    }

    private static string Build(string bodyHtml, bool darkTheme, bool live, string? fontFamily, double fontSize, bool inline = false, int previewWidthPx = DefaultPreviewWidthPx)
    {
        string theme = darkTheme ? "dark" : "light";
        string hljsCssFile = darkTheme ? "github-dark.min.css" : "github.min.css";
        string mermaidTheme = darkTheme ? "dark" : "default";
        string script = live ? LiveScript : StaticHighlightScript;
        // 0 or negative means "no limit" — let the reading column fill the available width.
        string widthCss = previewWidthPx > 0 ? previewWidthPx.ToString(CultureInfo.InvariantCulture) + "px" : "none";
        string css = Css
            .Replace("__FONT_FAMILY__", BuildFontFamilyCss(fontFamily))
            .Replace("__FONT_SIZE__", fontSize.ToString(CultureInfo.InvariantCulture) + "px")
            .Replace("__PREVIEW_WIDTH__", widthCss);

        string hljsCssTag = inline
            ? "<style>" + LoadEmbeddedText(hljsCssFile) + "</style>\n"
            : "<link rel=\"stylesheet\" href=\"" + AssetsOrigin + hljsCssFile + "\">\n";
        string highlightJsTag = inline
            ? "<script>" + LoadEmbeddedText("highlight.min.js") + "</script>\n"
            : "<script src=\"" + AssetsOrigin + "highlight.min.js\"></script>\n";
        // Mermaid runs fully offline too — the diagram source is whatever text sat inside
        // the ```mermaid fence (see MermaidExtension), rendered client-side into inline SVG
        // the same way highlight.js colors normal code fences.
        string mermaidJsTag = inline
            ? "<script>" + LoadEmbeddedText("mermaid.min.js") + "</script>\n"
            : "<script src=\"" + AssetsOrigin + "mermaid.min.js\"></script>\n";

        // Built with plain concatenation (not an interpolated string) so that embedded
        // JS/CSS content is never mistaken for C# raw-string interpolation syntax.
        return "<!doctype html>\n"
            + "<html data-theme=\"" + theme + "\">\n"
            + "<head>\n"
            + "<meta charset=\"utf-8\">\n"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n"
            + hljsCssTag
            + "<style>" + css + "</style>\n"
            + highlightJsTag
            + mermaidJsTag
            + "<script>mermaid.initialize({ startOnLoad: false, theme: \"" + mermaidTheme + "\" });</script>\n"
            + PanZoomScript
            + ScrollSyncScript
            // Exported HTML sits next to the .md, where plain relative paths already work.
            + (inline ? string.Empty : DocImageScript)
            + "</head>\n"
            + "<body>\n"
            + "<article id=\"content\" class=\"markdown-body\">" + bodyHtml + "</article>\n"
            + script
            + "\n</body>\n</html>\n";
    }

    private static string LoadEmbeddedText(string fileName)
    {
        var assembly = typeof(HtmlTemplate).Assembly;
        string resourceName = $"{assembly.GetName().Name}.Assets.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string BuildFontFamilyCss(string? fontFamily)
    {
        string family = string.IsNullOrWhiteSpace(fontFamily) ? DefaultFontFamily : fontFamily.Replace("\"", "'");
        return "\"" + family + "\", -apple-system, \"Segoe UI\", \"Microsoft JhengHei\", Helvetica, Arial, sans-serif";
    }

    private const string LiveScript = """
        <script>
          // async so that ExecuteScriptAsync (which awaits a returned Promise) doesn't
          // resolve — and e.g. let a PDF export proceed — until Mermaid has actually
          // finished drawing any diagrams in this update.
          async function updateContent(html) {
            document.getElementById('content').innerHTML = html;
            if (window.mfResolveImages) mfResolveImages();
            highlightAll();
            await renderMermaid();
            // Re-affirming the current scroll position can still shift it (the page's
            // height just changed), so suppress the resulting 'scroll' event — otherwise
            // every keystroke would echo a bogus position back to the editor.
            window.__mfSyncingScroll = true;
            window.scrollTo(window.scrollX, window.scrollY);
            requestAnimationFrame(() => { window.__mfSyncingScroll = false; });
          }
          function highlightAll() {
            if (window.hljs) {
              document.querySelectorAll('pre code').forEach((el) => {
                hljs.highlightElement(el);
              });
            }
          }
          async function renderMermaid() {
            if (!window.mermaid) return;
            // innerHTML was just replaced above, so every pre.mermaid node here is fresh
            // (never seen by mermaid.run before) — no data-processed bookkeeping needed.
            const nodes = document.querySelectorAll('#content pre.mermaid');
            if (nodes.length === 0) return;
            try {
              await mermaid.run({ nodes });
            } catch (e) {
              console.error('Mermaid render failed', e);
            }
            mfEnhanceMermaid(nodes);
          }
          window.addEventListener('load', () => { highlightAll(); renderMermaid(); });
        </script>
        """;

    private const string DocImageScript = """
        <script>
          function mfResolveImages() {
            const base = 'https://markforge.doc/';
            document.querySelectorAll('#content img[src], #content video[src], #content source[src]').forEach(function (el) {
              let src = el.getAttribute('src');
              if (!src || src.indexOf(base) === 0) return;
              let raw = src;
              try { raw = decodeURIComponent(src); } catch (e) { }
              if (/^file:\/\/\//i.test(raw)) raw = raw.substring(8);
              if (/^[a-z]:[\\/]/i.test(raw)) {
                el.setAttribute('src', base + '__abs__/' + encodeURI(raw.replace(/\\/g, '/')));
                return;
              }
              if (/^(?:[a-z][a-z0-9+.-]*:|\/|#)/i.test(src)) return;
              el.setAttribute('src', base + src.replace(/^\.\//, ''));
            });
          }
          document.addEventListener('DOMContentLoaded', mfResolveImages);
        </script>

        """;

    private const string StaticHighlightScript = """
        <script>
          window.addEventListener('load', async function () {
            if (window.hljs) {
              document.querySelectorAll('pre code').forEach((el) => hljs.highlightElement(el));
            }
            if (window.mermaid) {
              const nodes = document.querySelectorAll('pre.mermaid');
              if (nodes.length > 0) {
                try {
                  await mermaid.run({ nodes });
                } catch (e) {
                  console.error('Mermaid render failed', e);
                }
                mfEnhanceMermaid(nodes);
              }
            }
          });
        </script>
        """;

    // Mermaid's own useMaxWidth scaling shrinks large diagrams down to the column width,
    // which is often too small to read — and the browser's native Ctrl+wheel page zoom has
    // no independent panning, anchors oddly once content re-renders, and caps out well before
    // a big diagram becomes legible. This gives each diagram its own cursor-anchored wheel
    // zoom (no Ctrl needed) plus click-drag panning in any direction, scoped to that diagram
    // only so it never fights the page's own scrolling. State resets on re-render (each
    // rendered node is fresh), which is fine since a full content swap already jumps scroll
    // position anyway.
    private const string PanZoomScript = """
        <script>
          function mfApplyTransform(svg, st) {
            svg.style.transformOrigin = '0 0';
            svg.style.transform = 'translate(' + st.tx + 'px, ' + st.ty + 'px) scale(' + st.scale + ')';
          }
          function mfSetupPanZoom(pre) {
            const svg = pre.querySelector('svg');
            if (!svg) return;
            const st = { scale: 1, tx: 0, ty: 0 };
            mfApplyTransform(svg, st);

            pre.addEventListener('wheel', function (e) {
              e.preventDefault();
              const rect = pre.getBoundingClientRect();
              const cx = e.clientX - rect.left;
              const cy = e.clientY - rect.top;
              const factor = e.deltaY < 0 ? 1.15 : (1 / 1.15);
              const next = Math.min(12, Math.max(0.2, st.scale * factor));
              st.tx = cx - (cx - st.tx) * (next / st.scale);
              st.ty = cy - (cy - st.ty) * (next / st.scale);
              st.scale = next;
              mfApplyTransform(svg, st);
            }, { passive: false });

            pre.addEventListener('mousedown', function (e) {
              if (e.button !== 0) return;
              window.__mfDrag = { svg, st, startX: e.clientX, startY: e.clientY, startTx: st.tx, startTy: st.ty, pre };
              pre.style.cursor = 'grabbing';
              e.preventDefault();
            });

            pre.addEventListener('dblclick', function () {
              st.scale = 1; st.tx = 0; st.ty = 0;
              mfApplyTransform(svg, st);
            });
          }
          function mfEnhanceMermaid(nodes) {
            nodes.forEach(function (pre) { mfSetupPanZoom(pre); });
          }
          if (!window.__mfPanZoomGlobalInit) {
            window.__mfPanZoomGlobalInit = true;
            window.addEventListener('mousemove', function (e) {
              const d = window.__mfDrag;
              if (!d) return;
              d.st.tx = d.startTx + (e.clientX - d.startX);
              d.st.ty = d.startTy + (e.clientY - d.startY);
              mfApplyTransform(d.svg, d.st);
            });
            window.addEventListener('mouseup', function () {
              const d = window.__mfDrag;
              if (!d) return;
              d.pre.style.cursor = 'grab';
              window.__mfDrag = null;
            });
          }
        </script>
        """;

    // Two-way scroll sync with the Editor's Scintilla pane (Viewer has no second pane, so
    // this just sits inert there). __mfApplyScrollLine is called from C# to drive this page's
    // scroll from the editor's current source line; the 'scroll' listener reports this page's
    // own scroll back to C# (as a source line) via postMessage. __mfSyncingScroll suppresses
    // the echo either direction would otherwise cause when a scroll was just applied
    // programmatically.
    //
    // A single document-wide scroll ratio drifts badly the moment source and rendered output
    // stop growing at the same rate — a ```mermaid fence a few lines long can render as a
    // diagram hundreds of pixels tall. Instead this builds an ordered list of "landmarks"
    // (headings from SourceSyncExtension, code/Mermaid blocks from MermaidExtension — each
    // carries a data-src-line, and blocks additionally a data-src-end) pairing a source line
    // with its rendered pixel position, and interpolates between whichever pair brackets the
    // current position. Code/Mermaid blocks are "atomic": while the position falls anywhere
    // inside one, it pins to that block's start rather than smoothly advancing through it —
    // there's nothing meaningfully finer-grained inside a diagram to sync to.
    private const string ScrollSyncScript = """
        <script>
          window.__mfSyncingScroll = false;

          function mfGetLandmarks() {
            const nodes = document.querySelectorAll('#content [data-src-line]');
            const seen = new Set();
            const list = [];
            nodes.forEach(function (el) {
              const container = el.closest('pre') || el;
              if (seen.has(container)) return;
              seen.add(container);
              const line = parseInt(el.getAttribute('data-src-line'), 10);
              if (Number.isNaN(line)) return;
              const endAttr = el.getAttribute('data-src-end');
              const atomic = container.tagName === 'PRE';
              const top = container.offsetTop;
              list.push({
                line: line,
                endLine: endAttr !== null ? parseInt(endAttr, 10) : line,
                top: top,
                bottom: atomic ? top + container.offsetHeight : top,
                atomic: atomic,
              });
            });
            list.sort(function (a, b) { return a.line - b.line; });
            return list;
          }

          // forward: true maps a source line -> a pixel offset; false maps a pixel offset ->
          // a source line. Same bracket-and-interpolate walk either way, just swapping axes.
          function mfInterpolate(marks, value, forward) {
            if (marks.length === 0) return 0;
            for (let i = 0; i < marks.length; i++) {
              const m = marks[i];
              const mFrom = forward ? m.line : m.top;
              const mTo = forward ? m.top : m.line;
              const mFromEnd = forward ? m.endLine : m.bottom;
              if (m.atomic && value >= mFrom && value <= mFromEnd) return mTo;
              const next = marks[i + 1];
              if (!next) return mTo;
              const nFrom = forward ? next.line : next.top;
              const nTo = forward ? next.top : next.line;
              if (value >= mFrom && value <= nFrom) {
                const span = nFrom - mFrom;
                const t = span > 0 ? (value - mFrom) / span : 0;
                return mTo + t * (nTo - mTo);
              }
            }
            const first = marks[0];
            const fFrom = forward ? first.line : first.top;
            const fTo = forward ? first.top : first.line;
            const t = fFrom > 0 ? value / fFrom : 0;
            return t * fTo;
          }

          window.__mfApplyScrollLine = function (sourceLine) {
            const marks = mfGetLandmarks();
            const maxScroll = Math.max(0, document.documentElement.scrollHeight - window.innerHeight);
            const target = Math.max(0, Math.min(mfInterpolate(marks, sourceLine, true), maxScroll));
            window.__mfSyncingScroll = true;
            window.scrollTo(0, target);
            requestAnimationFrame(() => { window.__mfSyncingScroll = false; });
          };

          let mfScrollTicking = false;
          window.addEventListener('scroll', function () {
            if (window.__mfSyncingScroll || mfScrollTicking) return;
            mfScrollTicking = true;
            requestAnimationFrame(function () {
              mfScrollTicking = false;
              const marks = mfGetLandmarks();
              const line = Math.max(0, Math.round(mfInterpolate(marks, window.scrollY, false)));
              if (window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage(String(line));
              }
            });
          }, { passive: true });
        </script>
        """;

    private const string Css = """
        :root {
          --mf-bg: #ffffff;
          --mf-fg: #1f2328;
          --mf-muted: #59636e;
          --mf-border: #d1d9e0;
          --mf-code-bg: #f6f8fa;
          --mf-link: #0969da;
          --mf-blockquote: #59636e;
          --mf-font-family: __FONT_FAMILY__;
          --mf-font-size: __FONT_SIZE__;
        }
        html[data-theme="dark"] {
          --mf-bg: #0d1117;
          --mf-fg: #e6edf3;
          --mf-muted: #9198a1;
          --mf-border: #30363d;
          --mf-code-bg: #161b22;
          --mf-link: #4493f8;
          --mf-blockquote: #9198a1;
        }
        * { box-sizing: border-box; }
        html {
          /* We own scroll position ourselves (see the scroll-sync script) — without this,
             the browser's automatic scroll anchoring fights it: as the Mermaid diagram
             finishes async-rendering and grows the page, Chromium silently shifts scrollY
             to keep whatever it anchored to in view, which our own sync script then reads
             back as a "user scroll" and chases, drifting the view away from where it should
             have settled (e.g. away from the top on first open). */
          overflow-anchor: none;
        }
        body {
          margin: 0;
          background: var(--mf-bg);
          color: var(--mf-fg);
        }
        .markdown-body {
          max-width: __PREVIEW_WIDTH__;
          margin: 0 auto;
          padding: 28px 32px 80px;
          font-family: var(--mf-font-family);
          font-size: var(--mf-font-size);
          line-height: 1.6;
          word-wrap: break-word;
        }
        .markdown-body h1, .markdown-body h2, .markdown-body h3,
        .markdown-body h4, .markdown-body h5, .markdown-body h6 {
          font-weight: 600;
          margin-top: 24px;
          margin-bottom: 16px;
          line-height: 1.25;
        }
        .markdown-body h1 { font-size: 2em; padding-bottom: .3em; border-bottom: 1px solid var(--mf-border); }
        .markdown-body h2 { font-size: 1.5em; padding-bottom: .3em; border-bottom: 1px solid var(--mf-border); }
        .markdown-body h3 { font-size: 1.25em; }
        .markdown-body h4 { font-size: 1em; }
        .markdown-body h5 { font-size: .875em; }
        .markdown-body h6 { font-size: .85em; color: var(--mf-muted); }
        .markdown-body p, .markdown-body ul, .markdown-body ol,
        .markdown-body blockquote, .markdown-body table, .markdown-body pre {
          margin-top: 0;
          margin-bottom: 16px;
        }
        .markdown-body a { color: var(--mf-link); text-decoration: none; }
        .markdown-body a:hover { text-decoration: underline; }
        .markdown-body ul, .markdown-body ol { padding-left: 2em; }
        .markdown-body li + li { margin-top: .25em; }
        .markdown-body li.task-list-item { list-style: none; margin-left: -1.4em; }
        .markdown-body blockquote {
          padding: 0 1em;
          color: var(--mf-blockquote);
          border-left: .25em solid var(--mf-border);
        }
        .markdown-body code, .markdown-body tt {
          background: var(--mf-code-bg);
          padding: .2em .4em;
          border-radius: 6px;
          font-family: Consolas, "Cascadia Code", "SF Mono", Menlo, monospace;
          font-size: 85%;
        }
        .markdown-body pre {
          background: var(--mf-code-bg);
          padding: 16px;
          overflow: auto;
          border-radius: 6px;
          line-height: 1.45;
        }
        .markdown-body pre code {
          background: transparent;
          padding: 0;
          font-size: 85%;
        }
        .markdown-body pre.mermaid {
          background: transparent;
          padding: 8px 0;
          text-align: center;
          line-height: normal;
          overflow: hidden;
          cursor: grab;
          touch-action: none;
        }
        .markdown-body pre.mermaid svg {
          transform-origin: 0 0;
        }
        .markdown-body table {
          border-collapse: collapse;
          width: 100%;
          display: block;
          overflow: auto;
        }
        .markdown-body table th, .markdown-body table td {
          border: 1px solid var(--mf-border);
          padding: 6px 13px;
        }
        .markdown-body table tr:nth-child(2n) {
          background: var(--mf-code-bg);
        }
        .markdown-body img { max-width: 100%; box-sizing: content-box; }
        .markdown-body hr {
          height: .25em;
          padding: 0;
          margin: 24px 0;
          background: var(--mf-border);
          border: 0;
        }
        .markdown-body input[type="checkbox"] { margin-right: .5em; }
        .markdown-body kbd {
          background: var(--mf-code-bg);
          border: 1px solid var(--mf-border);
          border-bottom-width: 2px;
          border-radius: 6px;
          padding: 2px 6px;
          font-family: Consolas, monospace;
          font-size: 85%;
        }
        """;
}
