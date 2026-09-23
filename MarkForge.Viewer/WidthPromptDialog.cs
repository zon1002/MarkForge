namespace MarkForge.Viewer;

/// <summary>A small modal dialog for typing in a custom preview width in pixels.</summary>
internal static class WidthPromptDialog
{
    /// <summary>Shows the dialog and returns the chosen width, or <c>null</c> if cancelled.</summary>
    public static int? Show(IWin32Window owner, int currentWidthPx)
    {
        using var dialog = new Form
        {
            Text = "自訂預覽寬度",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(300, 110),
        };

        var label = new Label { Text = "寬度（px）：", AutoSize = true, Location = new Point(16, 22) };
        var numeric = new NumericUpDown
        {
            Minimum = 320,
            Maximum = 4000,
            Increment = 20,
            Value = Math.Clamp(currentWidthPx, 320, 4000),
            Location = new Point(120, 18),
            Width = 160,
        };
        var ok = new Button { Text = "確定", DialogResult = DialogResult.OK, Location = new Point(112, 62), Width = 80 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(200, 62), Width = 80 };

        dialog.Controls.AddRange(new Control[] { label, numeric, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        return dialog.ShowDialog(owner) == DialogResult.OK ? (int)numeric.Value : null;
    }
}
