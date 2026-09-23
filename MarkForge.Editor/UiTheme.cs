using System.Runtime.InteropServices;

namespace MarkForge.Ui;

/// <summary>
/// Dark/light theming for the WinForms chrome around the editor and preview — menus,
/// toolbars, status bar, splitter, the window title bar and native scrollbars — so the
/// whole window matches the dark editor/preview instead of just the two content panes.
/// Shared by Editor and Viewer (linked into the Viewer project).
/// </summary>
internal static class UiTheme
{
    public static readonly Color DarkBackground = Color.FromArgb(0x0D, 0x11, 0x17);
    public static readonly Color DarkChrome = Color.FromArgb(0x16, 0x1B, 0x22);
    public static readonly Color DarkBorder = Color.FromArgb(0x30, 0x36, 0x3D);
    public static readonly Color DarkHover = Color.FromArgb(0x26, 0x2C, 0x36);
    public static readonly Color DarkPressed = Color.FromArgb(0x1F, 0x3A, 0x5F);
    public static readonly Color DarkText = Color.FromArgb(0xE6, 0xED, 0xF3);
    public static readonly Color DarkIcon = Color.FromArgb(0xC9, 0xD1, 0xD9);
    public static readonly Color LightIcon = Color.FromArgb(0x40, 0x40, 0x40);

    private static readonly ToolStripProfessionalRenderer DarkRenderer = new DarkToolStripRenderer();
    private static readonly ToolStripProfessionalRenderer LightRenderer = new();

    public static void ApplyToolStrip(ToolStrip strip, bool dark)
    {
        strip.Renderer = dark ? DarkRenderer : LightRenderer;
        if (dark)
        {
            strip.BackColor = DarkChrome;
            strip.ForeColor = DarkText;
        }
        else
        {
            strip.ResetBackColor();
            strip.ResetForeColor();
        }
        ApplyItems(strip.Items, dark);
    }

    private static void ApplyItems(ToolStripItemCollection items, bool dark)
    {
        foreach (ToolStripItem item in items)
        {
            if (dark) item.ForeColor = DarkText; else item.ResetForeColor();
            if (item is ToolStripDropDownItem dropDown)
            {
                dropDown.DropDown.Renderer = dark ? DarkRenderer : LightRenderer;
                if (dark) dropDown.DropDown.BackColor = DarkChrome; else dropDown.DropDown.ResetBackColor();
                ApplyItems(dropDown.DropDownItems, dark);
            }
        }
    }

    public static void ApplyContextMenu(ContextMenuStrip menu, bool dark) => ApplyToolStrip(menu, dark);

    // ----- Native bits -----

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    /// <summary>Dark title bar (Windows 10 20H1+ / 11). Silently does nothing elsewhere.</summary>
    public static void ApplyTitleBar(Form form, bool dark)
    {
        if (!form.IsHandleCreated) return;
        int value = dark ? 1 : 0;
        try
        {
            // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE; 19 was its pre-20H1 value.
            if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    /// <summary>Dark native scrollbars on a control that draws its own (Scintilla).</summary>
    public static void ApplyScrollbars(Control control, bool dark)
    {
        if (!control.IsHandleCreated) return;
        try
        {
            SetWindowTheme(control.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    private sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
    {
        public DarkToolStripRenderer() : base(new DarkColorTable()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? DarkText : Color.FromArgb(0x6E, 0x76, 0x81);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = DarkText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            // The default check glyph is black-on-transparent; draw a light one instead.
            var r = e.ImageRectangle;
            using var back = new SolidBrush(DarkPressed);
            e.Graphics.FillRectangle(back, r);
            using var pen = new Pen(DarkText, 1.6f);
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            e.Graphics.DrawLines(pen, new[]
            {
                new PointF(r.Left + r.Width * 0.22f, r.Top + r.Height * 0.52f),
                new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.72f),
                new PointF(r.Left + r.Width * 0.78f, r.Top + r.Height * 0.30f),
            });
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public DarkColorTable() { UseSystemColors = false; }

        public override Color MenuStripGradientBegin => DarkChrome;
        public override Color MenuStripGradientEnd => DarkChrome;
        public override Color ToolStripGradientBegin => DarkChrome;
        public override Color ToolStripGradientMiddle => DarkChrome;
        public override Color ToolStripGradientEnd => DarkChrome;
        public override Color ToolStripBorder => DarkChrome;
        public override Color ToolStripDropDownBackground => DarkChrome;
        public override Color ToolStripContentPanelGradientBegin => DarkChrome;
        public override Color ToolStripContentPanelGradientEnd => DarkChrome;
        public override Color ToolStripPanelGradientBegin => DarkChrome;
        public override Color ToolStripPanelGradientEnd => DarkChrome;
        public override Color StatusStripGradientBegin => DarkChrome;
        public override Color StatusStripGradientEnd => DarkChrome;
        public override Color ImageMarginGradientBegin => DarkChrome;
        public override Color ImageMarginGradientMiddle => DarkChrome;
        public override Color ImageMarginGradientEnd => DarkChrome;
        public override Color MenuBorder => DarkBorder;
        public override Color MenuItemBorder => DarkBorder;
        public override Color MenuItemSelected => DarkHover;
        public override Color MenuItemSelectedGradientBegin => DarkHover;
        public override Color MenuItemSelectedGradientEnd => DarkHover;
        public override Color MenuItemPressedGradientBegin => DarkChrome;
        public override Color MenuItemPressedGradientMiddle => DarkChrome;
        public override Color MenuItemPressedGradientEnd => DarkChrome;
        public override Color ButtonSelectedHighlight => DarkHover;
        public override Color ButtonSelectedHighlightBorder => DarkBorder;
        public override Color ButtonSelectedBorder => DarkBorder;
        public override Color ButtonSelectedGradientBegin => DarkHover;
        public override Color ButtonSelectedGradientMiddle => DarkHover;
        public override Color ButtonSelectedGradientEnd => DarkHover;
        public override Color ButtonPressedHighlight => DarkPressed;
        public override Color ButtonPressedBorder => DarkBorder;
        public override Color ButtonPressedGradientBegin => DarkPressed;
        public override Color ButtonPressedGradientMiddle => DarkPressed;
        public override Color ButtonPressedGradientEnd => DarkPressed;
        public override Color ButtonCheckedHighlight => DarkPressed;
        public override Color ButtonCheckedGradientBegin => DarkPressed;
        public override Color ButtonCheckedGradientMiddle => DarkPressed;
        public override Color ButtonCheckedGradientEnd => DarkPressed;
        public override Color CheckBackground => DarkPressed;
        public override Color CheckSelectedBackground => DarkPressed;
        public override Color CheckPressedBackground => DarkPressed;
        public override Color SeparatorDark => DarkBorder;
        public override Color SeparatorLight => DarkChrome;
        public override Color GripDark => DarkBorder;
        public override Color GripLight => DarkChrome;
        public override Color OverflowButtonGradientBegin => DarkChrome;
        public override Color OverflowButtonGradientMiddle => DarkChrome;
        public override Color OverflowButtonGradientEnd => DarkChrome;
    }
}
