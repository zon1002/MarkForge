using System.Globalization;
using MarkForge.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using ScintillaNET;

namespace MarkForge.Editor;

public partial class MainForm : Form
{
    private readonly Scintilla _editor;
    private readonly WebView2 _preview;
    private readonly SplitContainer _split;
    private readonly System.Windows.Forms.Timer _previewDebounceTimer;
    private readonly System.Windows.Forms.Timer _scrollSettleTimer;
    private readonly StatusStrip _statusStrip;
    private readonly ToolStripStatusLabel _statusPosition;
    private readonly ToolStripStatusLabel _statusWordCount;
    private readonly ToolStripStatusLabel _statusFile;

    private string? _currentFilePath;
    private bool _isDirty;
    private bool _darkTheme;
    private bool _previewReady;
    private bool _suppressDirty;
    private bool _suppressEditorScrollSync;
    private readonly string? _initialFilePath;

    private string _editorFontFamily = "Consolas";
    private float _editorFontSize = 11f;
    private string _previewFontFamily = HtmlTemplate.DefaultFontFamily;
    private double _previewFontSize = HtmlTemplate.DefaultFontSize;
    private int _previewWidthPx = HtmlTemplate.DefaultPreviewWidthPx;

    public MainForm(string? openPath = null)
    {
        _initialFilePath = openPath;

        Text = "MarkForge – Markdown 編輯器";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TryLoadApplicationIcon();

        _editor = BuildEditor();
        ApplyEditorAppearance();
        _preview = new WebView2 { Dock = DockStyle.Fill };

        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterWidth = 6,
        };
        _split.Panel1.Controls.Add(_editor);
        _split.Panel2.Controls.Add(_preview);

        _previewDebounceTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _previewDebounceTimer.Tick += (_, _) =>
        {
            _previewDebounceTimer.Stop();
            _ = PushPreviewAsync();
        };

        _scrollSettleTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _scrollSettleTimer.Tick += (_, _) =>
        {
            _scrollSettleTimer.Stop();
            _suppressEditorScrollSync = false;
        };

        _statusStrip = new StatusStrip();
        _statusPosition = new ToolStripStatusLabel { Text = "行 1，列 1" };
        _statusWordCount = new ToolStripStatusLabel { Text = "0 字" };
        _statusFile = new ToolStripStatusLabel { Text = "新檔案", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        _statusStrip.Items.AddRange(new ToolStripItem[] { _statusFile, _statusWordCount, _statusPosition });

        var menu = BuildMenu();
        var toolbar = BuildToolbar();

        MainMenuStrip = menu;
        Controls.Add(_split);
        Controls.Add(toolbar);
        Controls.Add(menu);
        Controls.Add(_statusStrip);

        AllowDrop = true;
        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;

        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
    }

    private static Icon? TryLoadApplicationIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async void MainForm_Load(object? sender, EventArgs e)
    {
        _split.SplitterDistance = _split.Width / 2;

        await InitializePreviewAsync();

        if (!string.IsNullOrWhiteSpace(_initialFilePath) && File.Exists(_initialFilePath))
        {
            LoadFile(_initialFilePath);
        }
        else
        {
            NewDocument();
        }
    }

    private async Task InitializePreviewAsync()
    {
        try
        {
            // WebView2 defaults its user-data folder to the exe's own directory, which is
            // read-only once installed under Program Files. Point it at a writable per-user
            // folder instead, or CoreWebView2 initialization throws (COMException E_UNEXPECTED).
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarkForge", "Editor", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);

            await _preview.EnsureCoreWebView2Async(environment);

            string assetsFolder = HtmlTemplate.EnsureAssetsExtracted("Editor");
            _preview.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HtmlTemplate.AssetsHostName, assetsFolder, CoreWebView2HostResourceAccessKind.Allow);

            _preview.CoreWebView2.NavigationCompleted += (_, _) =>
            {
                _previewReady = true;
                _ = PushPreviewAsync();
            };
            _preview.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                string? raw = e.TryGetWebMessageAsString();
                if (raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int line))
                {
                    ApplyEditorScrollLine(line);
                }
            };
            _preview.CoreWebView2.NavigateToString(HtmlTemplate.Shell(_darkTheme, _previewFontFamily, _previewFontSize, _previewWidthPx));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                this,
                "找不到 WebView2 Runtime，即時預覽功能將無法使用。\n" +
                "請安裝「Microsoft Edge WebView2 Runtime」後重新啟動程式。",
                "預覽引擎不可用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "無法初始化即時預覽元件，此功能將無法使用，但編輯與存檔仍可正常運作。\n\n" + ex.Message,
                "預覽引擎初始化失敗",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void SchedulePreviewUpdate()
    {
        _previewDebounceTimer.Stop();
        _previewDebounceTimer.Start();
    }

    private async Task PushPreviewAsync()
    {
        if (!_previewReady || _preview.CoreWebView2 is null) return;

        string bodyHtml = MarkdownRenderer.RenderBody(_editor.Text);
        string script = $"updateContent({HtmlTemplate.ToJsString(bodyHtml)})";
        try
        {
            await _preview.CoreWebView2.ExecuteScriptAsync(script);
        }
        catch (ObjectDisposedException)
        {
            // Form closed mid-flight; ignore.
        }
    }

    /// <summary>Pushes the editor's current top source line to the preview pane, which maps it
    /// to a rendered pixel position via its heading/code-block landmark table.</summary>
    private void SyncPreviewScrollFromEditor()
    {
        if (_preview.CoreWebView2 is null || _editor.Lines.Count == 0) return;

        int docLine = _editor.DocLineFromVisible(_editor.FirstVisibleLine);
        _ = _preview.CoreWebView2.ExecuteScriptAsync(
            "window.__mfApplyScrollLine && window.__mfApplyScrollLine(" +
            docLine.ToString(CultureInfo.InvariantCulture) + ");");
    }

    /// <summary>Scrolls the editor to a source line the preview computed from its own scroll
    /// position via the same landmark table.</summary>
    private void ApplyEditorScrollLine(int line)
    {
        if (_editor.Lines.Count == 0) return;

        int clamped = Math.Clamp(line, 0, _editor.Lines.Count - 1);
        _suppressEditorScrollSync = true;
        _editor.FirstVisibleLine = _editor.Lines[clamped].DisplayIndex;
        _suppressEditorScrollSync = false;
    }

    /// <summary>
    /// Suppresses editor->preview scroll sync until it's been quiet for a moment after a
    /// full-text load. Scintilla's word-wrap and markdown-lexer styling for the new text
    /// aren't computed instantly — they settle over the following stretch via idle-time
    /// processing — and while that's happening, FirstVisibleLine can drift on its own (no user
    /// input involved) as the wrap/line-height layout it depends on keeps shifting underneath
    /// it. Without this, that drift was getting pushed straight to the preview, which could
    /// bounce back through the preview->editor side too, dragging both panes away from line 0
    /// within moments of opening a file. How long the churn actually lasts varies (document
    /// size, system load), so rather than guess a fixed delay, the UpdateUI handler in
    /// MainForm.Formatting.cs keeps pushing _scrollSettleTimer back out for as long as VScroll
    /// events keep arriving — sync only resumes once they've stopped for a full interval.
    /// </summary>
    private void BeginScrollSyncSettleWindow()
    {
        _suppressEditorScrollSync = true;
        _scrollSettleTimer.Stop();
        _scrollSettleTimer.Start();
    }

    private void ToggleTheme()
    {
        _darkTheme = !_darkTheme;
        _previewReady = false;
        _preview.CoreWebView2?.NavigateToString(HtmlTemplate.Shell(_darkTheme, _previewFontFamily, _previewFontSize, _previewWidthPx));
        ApplyEditorAppearance();
    }

    private void ChoosePreviewFont()
    {
        using var dialog = new FontDialog
        {
            Font = new Font(_previewFontFamily, (float)_previewFontSize),
            ShowEffects = false,
            FontMustExist = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _previewFontFamily = dialog.Font.Name;
        _previewFontSize = dialog.Font.Size;
        _previewReady = false;
        _preview.CoreWebView2?.NavigateToString(HtmlTemplate.Shell(_darkTheme, _previewFontFamily, _previewFontSize, _previewWidthPx));
    }

    /// <summary>Sets the preview's reading-column width. 0 (or less) removes the limit
    /// entirely, letting content fill the pane.</summary>
    private void SetPreviewWidth(int widthPx)
    {
        if (_previewWidthPx == widthPx) return;

        _previewWidthPx = widthPx;
        _previewReady = false;
        _preview.CoreWebView2?.NavigateToString(HtmlTemplate.Shell(_darkTheme, _previewFontFamily, _previewFontSize, _previewWidthPx));
    }
}
