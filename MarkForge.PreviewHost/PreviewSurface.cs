using System.Diagnostics;
using System.Runtime.InteropServices;
using MarkForge.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace MarkForge.PreviewHost;

/// <summary>
/// A child window created directly inside Explorer's preview pane, showing the rendered
/// Markdown in WebView2 — the same renderer, Mermaid and highlight.js setup as MarkForge
/// Viewer.
///
/// It's a plain container control rather than a Form on purpose: a Form re-parented into
/// another process's window doesn't stay there (WinForms recreates its handle while showing
/// it and the new window comes back top-level and hidden), whereas a control whose
/// CreateParams.Parent is the pane is simply created as that pane's child.
///
/// Selecting a file in Explorer is enough to preview it, so the page is locked down: no
/// navigation away (links open in the default browser instead), nothing fetched from the
/// network (remote images included — a tracking-pixel risk), no dev tools or browser
/// shortcuts. Scripts stay on because Mermaid and highlight.js need them.
/// </summary>
internal sealed class PreviewSurface : ContainerControl
{
    /// <summary>Larger files get a notice instead of a (slow) render.</summary>
    private const long MaxFileBytes = 8 * 1024 * 1024;

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPCHILDREN = 0x02000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const int WM_CLOSE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    private readonly string _filePath;
    private readonly IntPtr _parent;
    private readonly Rectangle _initialBounds;
    private readonly bool _dark;
    private readonly WebView2 _view;
    private readonly System.Windows.Forms.Timer _parentWatch;
    private bool _allowNextNavigation;

    public PreviewSurface(string filePath, IntPtr parent, Rectangle bounds)
    {
        _filePath = filePath;
        _parent = parent;
        _initialBounds = bounds;
        // Explorer itself follows the Windows "app mode" setting, so the preview does too.
        _dark = WindowsUsesDarkApps();

        BackColor = _dark ? Color.FromArgb(0x0D, 0x11, 0x17) : Color.White;
        _view = new WebView2
        {
            Dock = DockStyle.Fill,
            AllowExternalDrop = false,
            DefaultBackgroundColor = BackColor,
        };
        Controls.Add(_view);

        // Explorer normally tells the handler to Unload (which closes us), but if Explorer
        // or prevhost dies first nobody would — so leave on our own once the pane is gone.
        _parentWatch = new System.Windows.Forms.Timer { Interval = 1000 };
        _parentWatch.Tick += (_, _) =>
        {
            if (!IsWindow(_parent)) Environment.Exit(0);
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Parent = _parent;
            cp.Style = WS_CHILD | WS_VISIBLE | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
            cp.ExStyle = 0;
            cp.X = _initialBounds.X;
            cp.Y = _initialBounds.Y;
            cp.Width = _initialBounds.Width;
            cp.Height = _initialBounds.Height;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _parentWatch.Start();
        BeginInvoke(async () =>
        {
            try
            {
                await InitializeAsync();
            }
            catch (Exception ex)
            {
                Program.LogError(ex, "InitializeAsync");
                Application.ExitThread();
            }
        });
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        base.OnHandleDestroyed(e);
        Application.ExitThread();
    }

    protected override void WndProc(ref Message m)
    {
        // The preview handler asks us to go with WM_CLOSE on Unload (and terminates us if we
        // take longer than 500 ms). Nothing here needs saving and an orderly WebView2
        // teardown takes longer than that, so just leave — WebView2's own processes notice
        // their host is gone and exit by themselves.
        if (m.Msg == WM_CLOSE)
        {
            Visible = false;
            Environment.Exit(0);
        }
        base.WndProc(ref m);
    }

    private async Task InitializeAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarkForge", "PreviewHost", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);
            await _view.EnsureCoreWebView2Async(environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException)
        {
            Program.LogError(ex, "WebView2 init");
            ShowNotice("無法預覽：找不到 Microsoft Edge WebView2 Runtime。");
            return;
        }

        var core = _view.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;

        string assetsFolder = HtmlTemplate.EnsureAssetsExtracted("PreviewHost");
        core.SetVirtualHostNameToFolderMapping(HtmlTemplate.AssetsHostName, assetsFolder, CoreWebView2HostResourceAccessKind.Allow);
        string? documentFolder = Path.GetDirectoryName(Path.GetFullPath(_filePath));
        if (documentFolder is not null)
            core.SetVirtualHostNameToFolderMapping(HtmlTemplate.DocumentHostName, documentFolder, CoreWebView2HostResourceAccessKind.Allow);

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (!IsLocalResource(e.Request.Uri))
                e.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (_allowNextNavigation)
            {
                _allowNextNavigation = false;
                return;
            }
            e.Cancel = true;
            OpenExternally(e.Uri);
        };
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            OpenExternally(e.Uri);
        };

        Render();
    }

    private void Render()
    {
        string html;
        try
        {
            var info = new FileInfo(_filePath);
            if (info.Length > MaxFileBytes)
            {
                ShowNotice($"檔案過大（{info.Length / 1024 / 1024} MB），請以 MarkForge Viewer 開啟。");
                return;
            }

            string markdown;
            // Explorer may preview a file another program still has open for writing.
            using (var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true))
            {
                markdown = reader.ReadToEnd();
            }
            html = MarkdownRenderer.RenderDocument(markdown, _dark, previewWidthPx: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice("無法讀取檔案：" + ex.Message);
            return;
        }

        _allowNextNavigation = true;
        _view.CoreWebView2.NavigateToString(html);
    }

    private void ShowNotice(string message)
    {
        _view.Visible = false;
        Controls.Add(new Label
        {
            Text = message,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = _dark ? Color.FromArgb(0xC9, 0xD1, 0xD9) : Color.FromArgb(0x40, 0x40, 0x40),
        });
    }

    /// <summary>The app's own asset/document hosts and inline data only — anything else
    /// (http images, web fonts, iframes) is refused.</summary>
    private static bool IsLocalResource(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is "data" or "blob" or "about") return true;
        return parsed.Scheme == Uri.UriSchemeHttps
            && (parsed.Host.Equals(HtmlTemplate.AssetsHostName, StringComparison.OrdinalIgnoreCase)
                || parsed.Host.Equals(HtmlTemplate.DocumentHostName, StringComparison.OrdinalIgnoreCase));
    }

    private static void OpenExternally(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return;
        if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeMailto) return;
        try
        {
            Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static bool WindowsUsesDarkApps()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _parentWatch.Dispose();
        base.Dispose(disposing);
    }
}
