using System.Globalization;
using MarkForge.Core;
using MarkForge.Ui;
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
    // Dark is the default look since v1.6.0; 檢視 > 切換深色主題 still switches to light.
    private bool _darkTheme = true;
    private bool _previewReady;
    private bool _suppressDirty;
    private bool _suppressEditorScrollSync;
    private readonly string? _initialFilePath;
    private readonly DocumentAssets _assets = new();
    private MenuStrip _menu = null!;
    private ToolStrip _toolbar = null!;
    private ContextMenuStrip _editorContextMenu = null!;

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
        _preview = new WebView2
        {
            Dock = DockStyle.Fill,
            // Files dropped on the preview used to make WebView2 navigate to them, replacing
            // the preview page; drops belong to the editor.
            AllowExternalDrop = false,
            DefaultBackgroundColor = _darkTheme ? UiTheme.DarkBackground : Color.White,
        };

        // ApplyEditorAppearance() above already forced Scintilla's window into existence, so
        // take over its drop target now; the handler covers any later handle recreation.
        _editor.HandleCreated += (_, _) =>
        {
            TakeOverEditorDropTarget();
            UiTheme.ApplyScrollbars(_editor, _darkTheme);
        };
        if (_editor.IsHandleCreated) TakeOverEditorDropTarget();
        _editor.DragEnter += Editor_DragEnter;
        _editor.DragOver += Editor_DragOver;
        _editor.DragDrop += Editor_DragDrop;
        _editor.DragLeave += (_, _) => _dragIsInternalMove = false;

        // Scintilla's built-in right-click menu pastes plain text, bypassing SmartPaste.
        _editor.UsePopup(false);
        _editorContextMenu = BuildEditorContextMenu();
        _editor.ContextMenuStrip = _editorContextMenu;

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

        _menu = BuildMenu();
        _toolbar = BuildToolbar();

        MainMenuStrip = _menu;
        Controls.Add(_split);
        Controls.Add(_toolbar);
        Controls.Add(_menu);
        Controls.Add(_statusStrip);
        ApplyChromeTheme();

        AllowDrop = true;
        DragEnter += MainForm_DragEnter;
        DragDrop += MainForm_DragDrop;

        Load += MainForm_Load;
        FormClosing += MainForm_FormClosing;
        FormClosed += (_, _) => _assets.Dispose();
        HandleCreated += (_, _) => UiTheme.ApplyTitleBar(this, _darkTheme);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Shift+Insert is the other classic paste key; route it through SmartPaste too.
        if (keyData == (Keys.Shift | Keys.Insert) && _editor.Focused)
        {
            SmartPaste();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
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

            // Relative images in the document are rewritten by the preview page to
            // https://markforge.doc/<path> and served here — from the .md file's folder, or
            // straight from wherever a dragged/pasted image still lives before it's saved.
            _preview.CoreWebView2.AddWebResourceRequestedFilter(
                "https://" + HtmlTemplate.DocumentHostName + "/*", CoreWebView2WebResourceContext.All);
            _preview.CoreWebView2.WebResourceRequested += (_, e) => ServeDocumentResource(environment, e);

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

    private void ServeDocumentResource(CoreWebView2Environment environment, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            string path = Uri.UnescapeDataString(new Uri(e.Request.Uri).AbsolutePath.TrimStart('/'));
            string? file = path.StartsWith(HtmlTemplate.AbsolutePathPrefix, StringComparison.Ordinal)
                ? path[HtmlTemplate.AbsolutePathPrefix.Length..].Replace('/', '\\')
                : _assets.Resolve(path);

            if (file is not null && File.Exists(file))
            {
                var stream = new MemoryStream(File.ReadAllBytes(file));
                e.Response = environment.CreateWebResourceResponse(
                    stream, 200, "OK", "Content-Type: " + HtmlTemplate.GuessContentType(file) + "\r\nCache-Control: no-store");
                return;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UriFormatException or ArgumentException)
        {
        }
        e.Response = environment.CreateWebResourceResponse(null, 404, "Not Found", string.Empty);
    }

    private void ToggleTheme()
    {
        var oldIconColor = IconColor;
        _darkTheme = !_darkTheme;
        _previewReady = false;
        _preview.DefaultBackgroundColor = _darkTheme ? UiTheme.DarkBackground : Color.White;
        _preview.CoreWebView2?.NavigateToString(HtmlTemplate.Shell(_darkTheme, _previewFontFamily, _previewFontSize, _previewWidthPx));
        ApplyEditorAppearance();

        IconProvider.Recolor(_menu.Items, oldIconColor, IconColor);
        IconProvider.Recolor(_toolbar.Items, oldIconColor, IconColor);
        IconProvider.Recolor(_editorContextMenu.Items, oldIconColor, IconColor);
        ApplyChromeTheme();
    }

    /// <summary>Menus, toolbar, status bar, splitter, title bar and scrollbars — everything
    /// around the two content panes.</summary>
    private void ApplyChromeTheme()
    {
        UiTheme.ApplyToolStrip(_menu, _darkTheme);
        UiTheme.ApplyToolStrip(_toolbar, _darkTheme);
        UiTheme.ApplyToolStrip(_statusStrip, _darkTheme);
        UiTheme.ApplyContextMenu(_editorContextMenu, _darkTheme);

        Color back = _darkTheme ? UiTheme.DarkBorder : SystemColors.Control;
        _split.BackColor = back;
        _split.Panel1.BackColor = _darkTheme ? UiTheme.DarkBackground : SystemColors.Window;
        _split.Panel2.BackColor = _darkTheme ? UiTheme.DarkBackground : SystemColors.Window;
        BackColor = _darkTheme ? UiTheme.DarkChrome : SystemColors.Control;

        UiTheme.ApplyTitleBar(this, _darkTheme);
        UiTheme.ApplyScrollbars(_editor, _darkTheme);
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
