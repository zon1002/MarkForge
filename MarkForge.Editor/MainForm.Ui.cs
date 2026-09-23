using MarkForge.Core;

namespace MarkForge.Editor;

public partial class MainForm
{
    private static readonly Color IconColor = Color.FromArgb(0x40, 0x40, 0x40);

    private static Image SvgIcon(string name, int size = 18) => IconProvider.Get(name, size, IconColor);
    private static Image SvgIcon(string name, int size, Color color) => IconProvider.Get(name, size, color);

    private MenuStrip BuildMenu()
    {
        var menu = new MenuStrip();

        var file = new ToolStripMenuItem("檔案(&F)");
        file.DropDownItems.Add(new ToolStripMenuItem("新增(&N)", SvgIcon("file-earmark-plus", 16), (_, _) => NewDocument()) { ShortcutKeys = Keys.Control | Keys.N });
        file.DropDownItems.Add(new ToolStripMenuItem("開啟(&O)...", SvgIcon("folder2-open", 16), (_, _) => OpenDocument()) { ShortcutKeys = Keys.Control | Keys.O });
        file.DropDownItems.Add(new ToolStripMenuItem("儲存(&S)", SvgIcon("save", 16), (_, _) => SaveDocument()) { ShortcutKeys = Keys.Control | Keys.S });
        file.DropDownItems.Add(new ToolStripMenuItem("另存新檔(&A)...", SvgIcon("save2", 16), (_, _) => SaveDocumentAs()) { ShortcutKeys = Keys.Control | Keys.Shift | Keys.S });
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("匯出為 HTML(&E)...", SvgIcon("filetype-html", 16), (_, _) => ExportHtml()));
        file.DropDownItems.Add(new ToolStripMenuItem("另存為 PDF(&P)...", SvgIcon("filetype-pdf", 16), (_, _) => ExportPdf()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(new ToolStripMenuItem("結束(&X)", SvgIcon("box-arrow-right", 16), (_, _) => Close()));

        var edit = new ToolStripMenuItem("編輯(&E)");
        edit.DropDownItems.Add(new ToolStripMenuItem("復原(&U)", SvgIcon("arrow-counterclockwise", 16), (_, _) => _editor.Undo()) { ShortcutKeys = Keys.Control | Keys.Z });
        edit.DropDownItems.Add(new ToolStripMenuItem("重做(&R)", SvgIcon("arrow-clockwise", 16), (_, _) => _editor.Redo()) { ShortcutKeys = Keys.Control | Keys.Y });
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(new ToolStripMenuItem("剪下(&T)", SvgIcon("scissors", 16), (_, _) => _editor.Cut()) { ShortcutKeys = Keys.Control | Keys.X });
        edit.DropDownItems.Add(new ToolStripMenuItem("複製(&C)", SvgIcon("copy", 16), (_, _) => _editor.Copy()) { ShortcutKeys = Keys.Control | Keys.C });
        edit.DropDownItems.Add(new ToolStripMenuItem("貼上(&P)", SvgIcon("clipboard", 16), (_, _) => _editor.Paste()) { ShortcutKeys = Keys.Control | Keys.V });
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(new ToolStripMenuItem("粗體(&B)", SvgIcon("type-bold", 16), (_, _) => ActionBold()) { ShortcutKeys = Keys.Control | Keys.B });
        edit.DropDownItems.Add(new ToolStripMenuItem("斜體(&I)", SvgIcon("type-italic", 16), (_, _) => ActionItalic()) { ShortcutKeys = Keys.Control | Keys.I });
        edit.DropDownItems.Add(new ToolStripMenuItem("插入連結(&K)", SvgIcon("link-45deg", 16), (_, _) => ActionLink()) { ShortcutKeys = Keys.Control | Keys.K });

        var view = new ToolStripMenuItem("檢視(&V)");
        view.DropDownItems.Add(new ToolStripMenuItem("切換深色主題(&D)", SvgIcon("moon-stars", 16), (_, _) => ToggleTheme()));
        view.DropDownItems.Add(new ToolStripSeparator());
        var showEditorItem = new ToolStripMenuItem("顯示編輯器(&E)", SvgIcon("layout-split", 16), (_, _) => ToggleEditorVisible());
        var showPreviewItem = new ToolStripMenuItem("顯示預覽(&P)", SvgIcon("layout-split", 16), (_, _) => TogglePreviewVisible());
        view.DropDownOpening += (_, _) =>
        {
            showEditorItem.Checked = !_split.Panel1Collapsed;
            showPreviewItem.Checked = !_split.Panel2Collapsed;
        };
        view.DropDownItems.Add(showEditorItem);
        view.DropDownItems.Add(showPreviewItem);
        view.DropDownItems.Add(new ToolStripSeparator());
        view.DropDownItems.Add(new ToolStripMenuItem("編輯器字型(&F)...", SvgIcon("fonts", 16), (_, _) => ChooseEditorFont()));
        view.DropDownItems.Add(new ToolStripMenuItem("預覽字型(&G)...", SvgIcon("fonts", 16), (_, _) => ChoosePreviewFont()));
        view.DropDownItems.Add(BuildPreviewWidthMenu());

        var help = new ToolStripMenuItem("說明(&H)");
        help.DropDownItems.Add(new ToolStripMenuItem("關於 MarkForge(&A)", SvgIcon("info-circle", 16), (_, _) => ShowAbout()));

        menu.Items.AddRange(new ToolStripItem[] { file, edit, view, help });
        return menu;
    }

    private ToolStrip BuildToolbar()
    {
        var tb = new ToolStrip { GripStyle = ToolStripGripStyle.Hidden, ImageScalingSize = new Size(18, 18) };

        ToolStripButton AddBtn(string icon, string text, string tip, EventHandler onClick, FontStyle style = FontStyle.Regular)
        {
            var b = new ToolStripButton(text, SvgIcon(icon))
            {
                ToolTipText = tip,
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
            };
            if (style != FontStyle.Regular)
                b.Font = new Font(tb.Font, style);
            b.Click += onClick;
            tb.Items.Add(b);
            return b;
        }

        AddBtn("type-bold", "粗體", "粗體 (Ctrl+B)", (_, _) => ActionBold(), FontStyle.Bold);
        AddBtn("type-italic", "斜體", "斜體 (Ctrl+I)", (_, _) => ActionItalic(), FontStyle.Italic);
        AddBtn("type-strikethrough", "刪除線", "刪除線", (_, _) => ActionStrikethrough());
        tb.Items.Add(new ToolStripSeparator());

        var headingBtn = new ToolStripDropDownButton("標題", SvgIcon("type-h1"));
        for (int level = 1; level <= 6; level++)
        {
            int captured = level;
            headingBtn.DropDownItems.Add(new ToolStripMenuItem($"標題 {captured}", null, (_, _) => ActionHeading(captured)));
        }
        tb.Items.Add(headingBtn);
        tb.Items.Add(new ToolStripSeparator());

        AddBtn("blockquote-left", "引用", "引用區塊", (_, _) => ActionBlockquote());
        AddBtn("list-ul", "項目清單", "項目符號清單", (_, _) => ActionBulletList());
        AddBtn("list-ol", "編號清單", "編號清單", (_, _) => ActionOrderedList());

        var taskListBtn = new ToolStripSplitButton("工作清單")
        {
            Image = SvgIcon("list-check"),
            ToolTipText = "工作清單（核取方塊）— 點箭頭可選擇打勾/打叉/三角形/驚嘆號",
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
        };
        taskListBtn.ButtonClick += (_, _) => ActionTaskList();
        taskListBtn.DropDownItems.Add(new ToolStripMenuItem("打勾", SvgIcon("check-circle-fill", 16, Color.FromArgb(0x16, 0xA3, 0x4A)), (_, _) => ActionTaskDone()));
        taskListBtn.DropDownItems.Add(new ToolStripMenuItem("打叉", SvgIcon("x-circle-fill", 16, Color.FromArgb(0xDC, 0x26, 0x26)), (_, _) => ActionTaskCancelled()));
        taskListBtn.DropDownItems.Add(new ToolStripMenuItem("三角形", SvgIcon("triangle-fill", 16, Color.FromArgb(0xF5, 0x9E, 0x0B)), (_, _) => ActionTaskPending()));
        taskListBtn.DropDownItems.Add(new ToolStripMenuItem("驚嘆號", SvgIcon("exclamation-circle-fill", 16, Color.FromArgb(0x25, 0x63, 0xEB)), (_, _) => ActionTaskImportant()));
        tb.Items.Add(taskListBtn);
        tb.Items.Add(new ToolStripSeparator());

        AddBtn("code-slash", "行內程式碼", "行內程式碼", (_, _) => ActionInlineCode());
        AddBtn("code-square", "程式碼區塊", "程式碼區塊", (_, _) => ActionCodeBlock());
        tb.Items.Add(new ToolStripSeparator());

        AddBtn("link-45deg", "連結", "插入連結 (Ctrl+K)", (_, _) => ActionLink());
        AddBtn("image", "圖片", "插入圖片", (_, _) => ActionImage());
        AddBtn("table", "表格", "插入表格", (_, _) => ActionTable());
        AddBtn("dash-lg", "分隔線", "插入水平分隔線", (_, _) => ActionHorizontalRule());
        tb.Items.Add(new ToolStripSeparator());

        AddBtn("arrow-counterclockwise", "復原", "復原 (Ctrl+Z)", (_, _) => _editor.Undo());
        AddBtn("arrow-clockwise", "重做", "重做 (Ctrl+Y)", (_, _) => _editor.Redo());

        return tb;
    }

    /// <summary>Preset preview reading-column widths (0 = unlimited) plus a "自訂寬度..." entry
    /// for any other value. The checkmark tracks _previewWidthPx and is kept in sync on every
    /// DropDownOpening rather than only when an item is clicked, so it still reflects reality
    /// if the width is ever changed some other way later.</summary>
    private ToolStripMenuItem BuildPreviewWidthMenu()
    {
        var widthMenu = new ToolStripMenuItem("預覽寬度(&W)", SvgIcon("layout-split", 16));
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
            var item = new ToolStripMenuItem(label, null, (_, _) => SetPreviewWidth(widthPx));
            items.Add((item, widthPx));
            widthMenu.DropDownItems.Add(item);
        }

        widthMenu.DropDownItems.Add(new ToolStripSeparator());
        var customItem = new ToolStripMenuItem("自訂寬度(&C)...", null, (_, _) =>
        {
            int? chosen = WidthPromptDialog.Show(this, _previewWidthPx > 0 ? _previewWidthPx : HtmlTemplate.DefaultPreviewWidthPx);
            if (chosen is int width) SetPreviewWidth(width);
        });
        widthMenu.DropDownItems.Add(customItem);

        widthMenu.DropDownOpening += (_, _) =>
        {
            bool matchesPreset = false;
            foreach (var (item, widthPx) in items)
            {
                item.Checked = widthPx == _previewWidthPx;
                matchesPreset |= item.Checked;
            }
            customItem.Checked = !matchesPreset;
            customItem.Text = matchesPreset ? "自訂寬度(&C)..." : $"自訂寬度(&C)...（目前 {_previewWidthPx}px）";
        };

        return widthMenu;
    }

    private void ToggleEditorVisible()
    {
        if (_split.Panel1Collapsed)
        {
            _split.Panel1Collapsed = false;
            return;
        }

        if (_split.Panel2Collapsed)
        {
            MessageBox.Show(this, "編輯器與預覽不能同時隱藏，至少需保留一個窗格。", "無法隱藏",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _split.Panel1Collapsed = true;
    }

    private void TogglePreviewVisible()
    {
        if (_split.Panel2Collapsed)
        {
            _split.Panel2Collapsed = false;
            return;
        }

        if (_split.Panel1Collapsed)
        {
            MessageBox.Show(this, "編輯器與預覽不能同時隱藏，至少需保留一個窗格。", "無法隱藏",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _split.Panel2Collapsed = true;
    }

    private void ShowAbout()
    {
        MessageBox.Show(
            this,
            "MarkForge Markdown 編輯器\n" +
            "使用 Scintilla 編輯核心與 WebView2 即時預覽\n" +
            "支援 CommonMark + GFM（表格、任務清單、刪除線等）",
            "關於 MarkForge",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }
}
