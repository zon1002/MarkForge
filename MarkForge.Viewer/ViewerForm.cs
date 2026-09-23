using MarkForge.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MarkForge.Viewer;

/// <summary>
/// A minimal, fast Markdown viewer: no editing, just render-and-watch.
/// Reloads automatically when the open file changes on disk, so it pairs well
/// with any external editor (including MarkForge.Editor) for a live "second screen" preview.
/// </summary>
public class ViewerForm : Form
{
    private readonly WebView2 _view;
    private readonly ToolStrip _toolbar;
    private readonly System.Windows.Forms.Timer _reloadDebounceTimer;
    private FileSystemWatcher? _watcher;

    private string? _currentFilePath;
    private bool _darkTheme;
    private readonly string? _initialFilePath;

    private string _fontFamily = HtmlTemplate.DefaultFontFamily;
    private double _fontSize = HtmlTemplate.DefaultFontSize;
    private int _widthPx = HtmlTemplate.DefaultPreviewWidthPx;

    public ViewerForm(string? openPath = null)
    {
        _initialFilePath = openPath;

        Text = "MarkForge Viewer";
        Width = 900;
        Height = 900;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = TryLoadApplicationIcon();

        _view = new WebView2 { Dock = DockStyle.Fill };
        _toolbar = BuildToolbar();

        _reloadDebounceTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _reloadDebounceTimer.Tick += (_, _) =>
        {
            _reloadDebounceTimer.Stop();
            ReloadFromDisk();
        };

        Controls.Add(_view);
        Controls.Add(_toolbar);

        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths) OpenFile(paths[0]);
        };

        KeyPreview = true;
        KeyDown += ViewerForm_KeyDown;

        Load += ViewerForm_Load;
        FormClosed += (_, _) => _watcher?.Dispose();
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

    private ToolStrip BuildToolbar()
    {
        var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden };

        var open = new ToolStripButton("開啟...") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "開啟 Markdown 檔案 (Ctrl+O)" };
        open.Click += (_, _) => PromptOpenFile();

        var reload = new ToolStripButton("重新整理") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "重新載入 (F5)" };
        reload.Click += (_, _) => ReloadFromDisk();

        var theme = new ToolStripButton("深色/淺色") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "切換主題" };
        theme.Click += (_, _) => ToggleTheme();

        var font = new ToolStripButton("字型...") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "選擇預覽字型與大小" };
        font.Click += (_, _) => ChooseFont();

        var width = BuildWidthMenu();

        var savePdf = new ToolStripButton("另存PDF...") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "將目前預覽內容另存為 PDF" };
        savePdf.Click += (_, _) => ExportPdf();

        tb.Items.AddRange(new ToolStripItem[] { open, reload, theme, font, width, savePdf });
        return tb;
    }

    /// <summary>Preset preview reading-column widths (0 = unlimited); mirrors the Editor's
    /// 檢視 &gt; 預覽寬度 menu. The checkmark is kept in sync on every DropDownOpening rather
    /// than only when an item is clicked, so it still reflects reality if _widthPx is ever
    /// changed some other way later.</summary>
    private ToolStripDropDownButton BuildWidthMenu()
    {
        var widthMenu = new ToolStripDropDownButton("寬度") { DisplayStyle = ToolStripItemDisplayStyle.Text, ToolTipText = "調整預覽的閱讀版面寬度" };
        (string Label, int WidthPx)[] presets =
        {
            ("860px（預設）", 860),
            ("1000px", 1000),
            ("1200px", 1200),
            ("1400px", 1400),
            ("不限制寬度", 0),
        };

        var items = new List<(ToolStripMenuItem Item, int WidthPx)>();
        foreach (var (label, widthPx) in presets)
        {
            var item = new ToolStripMenuItem(label, null, (_, _) => { _widthPx = widthPx; Render(); });
            items.Add((item, widthPx));
            widthMenu.DropDownItems.Add(item);
        }

        widthMenu.DropDownItems.Add(new ToolStripSeparator());
        var customItem = new ToolStripMenuItem("自訂寬度...", null, (_, _) =>
        {
            int? chosen = WidthPromptDialog.Show(this, _widthPx > 0 ? _widthPx : HtmlTemplate.DefaultPreviewWidthPx);
            if (chosen is int width) { _widthPx = width; Render(); }
        });
        widthMenu.DropDownItems.Add(customItem);

        widthMenu.DropDownOpening += (_, _) =>
        {
            bool matchesPreset = false;
            foreach (var (item, widthPx) in items)
            {
                item.Checked = widthPx == _widthPx;
                matchesPreset |= item.Checked;
            }
            customItem.Checked = !matchesPreset;
            customItem.Text = matchesPreset ? "自訂寬度..." : $"自訂寬度...（目前 {_widthPx}px）";
        };

        return widthMenu;
    }

    private async void ExportPdf()
    {
        if (_view.CoreWebView2 is null)
        {
            MessageBox.Show(this, "預覽引擎尚未就緒，無法匯出 PDF。", "匯出失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Filter = "PDF 檔案 (*.pdf)|*.pdf|所有檔案 (*.*)|*.*",
            FileName = _currentFilePath is null ? "未命名.pdf" : Path.ChangeExtension(Path.GetFileName(_currentFilePath), ".pdf"),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            bool success = await _view.CoreWebView2.PrintToPdfAsync(dialog.FileName);
            if (!success)
            {
                MessageBox.Show(this, "PDF 匯出未成功完成。", "匯出失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"無法匯出 PDF：\n{ex.Message}", "匯出失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ChooseFont()
    {
        using var dialog = new FontDialog
        {
            Font = new Font(_fontFamily, (float)_fontSize),
            ShowEffects = false,
            FontMustExist = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _fontFamily = dialog.Font.Name;
        _fontSize = dialog.Font.Size;
        Render();
    }

    private void ViewerForm_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.O) { PromptOpenFile(); e.Handled = true; }
        else if (e.KeyCode == Keys.F5) { ReloadFromDisk(); e.Handled = true; }
    }

    private async void ViewerForm_Load(object? sender, EventArgs e)
    {
        try
        {
            // WebView2 defaults its user-data folder to the exe's own directory, which is
            // read-only once installed under Program Files. Point it at a writable per-user
            // folder instead, or CoreWebView2 initialization throws (COMException E_UNEXPECTED).
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MarkForge", "Viewer", "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userDataFolder);

            await _view.EnsureCoreWebView2Async(environment);

            string assetsFolder = HtmlTemplate.EnsureAssetsExtracted("Viewer");
            _view.CoreWebView2.SetVirtualHostNameToFolderMapping(
                HtmlTemplate.AssetsHostName, assetsFolder, CoreWebView2HostResourceAccessKind.Allow);

            Render();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(
                this,
                "找不到 WebView2 Runtime，無法顯示預覽。\n請安裝「Microsoft Edge WebView2 Runtime」後重新啟動程式。",
                "預覽引擎不可用",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "無法初始化預覽元件。\n\n" + ex.Message,
                "預覽引擎初始化失敗",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (!string.IsNullOrWhiteSpace(_initialFilePath) && File.Exists(_initialFilePath))
        {
            OpenFile(_initialFilePath);
        }
    }

    private void PromptOpenFile()
    {
        using var dialog = new OpenFileDialog { Filter = "Markdown 檔案 (*.md;*.markdown)|*.md;*.markdown|所有檔案 (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            OpenFile(dialog.FileName);
        }
    }

    private void OpenFile(string path)
    {
        _currentFilePath = path;
        Text = $"{Path.GetFileName(path)} - MarkForge Viewer";
        Render();
        WatchFile(path);
    }

    private void ReloadFromDisk()
    {
        if (_currentFilePath is not null) Render();
    }

    private void Render()
    {
        if (_view.CoreWebView2 is null) return;

        if (_currentFilePath is null)
        {
            _view.CoreWebView2.NavigateToString(HtmlTemplate.Wrap(
                "<p><em>尚未開啟任何檔案。可拖放 .md 檔案到此視窗，或使用「開啟」按鈕。</em></p>",
                _darkTheme, _fontFamily, _fontSize, _widthPx));
            return;
        }

        try
        {
            string markdown = File.ReadAllText(_currentFilePath);
            string html = MarkdownRenderer.RenderDocument(markdown, _darkTheme, _fontFamily, _fontSize, _widthPx);
            _view.CoreWebView2.NavigateToString(html);
        }
        catch (IOException)
        {
            // File may be mid-write by another process; the next watcher tick will retry.
        }
    }

    private void WatchFile(string path)
    {
        _watcher?.Dispose();

        string? dir = Path.GetDirectoryName(path);
        if (dir is null) return;

        _watcher = new FileSystemWatcher(dir, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => BeginInvoke(ScheduleReload);
    }

    private void ScheduleReload()
    {
        _reloadDebounceTimer.Stop();
        _reloadDebounceTimer.Start();
    }

    private void ToggleTheme()
    {
        _darkTheme = !_darkTheme;
        Render();
    }
}
