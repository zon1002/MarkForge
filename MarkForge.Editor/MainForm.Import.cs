using System.Runtime.InteropServices;
using System.Text;
using MarkForge.Editor.Import;

namespace MarkForge.Editor;

/// <summary>
/// Everything that brings outside content into the document: pasting (Word / LibreOffice /
/// Google Docs formatting → Markdown, Excel / Calc cells → tables, screenshots → images)
/// and dropping files onto the editor (images and other attachments, CSV / Excel / ODS
/// → tables, .mmd → Mermaid blocks).
/// </summary>
public partial class MainForm
{
    private static readonly string[] MermaidExtensions = { ".mmd", ".mermaid" };
    private static readonly string[] MarkdownExtensions = { ".md", ".markdown" };

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);

    /// <summary>Remembers whether the current drag started inside the editor itself (a text
    /// move), decided once on DragEnter.</summary>
    private bool _dragIsInternalMove;

    // ----- Paste -----

    /// <summary>Ctrl+V: converts rich clipboard content to Markdown when there's something
    /// worth converting, otherwise falls back to Scintilla's ordinary text paste.</summary>
    private void SmartPaste()
    {
        IDataObject? data;
        try
        {
            data = Clipboard.GetDataObject();
        }
        catch (ExternalException)
        {
            return; // clipboard locked by another process
        }

        if (data is null || !InsertFromDataObject(data, position: null))
            _editor.Paste();
    }

    private void PastePlainText()
    {
        if (!Clipboard.ContainsText()) return;
        ReplaceSelectionWith(Clipboard.GetText().Replace("\r\n", "\n"), block: false);
    }

    /// <summary>
    /// Shared by paste and drop. Order matters: Excel and Word also put a picture of the
    /// selection on the clipboard, so the richer text formats are tried before images.
    /// </summary>
    /// <param name="position">Insertion point, or null to replace the current selection.</param>
    /// <returns>False when nothing here was special and a plain-text paste should happen.</returns>
    private bool InsertFromDataObject(IDataObject data, int? position)
    {
        if (data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            InsertFiles(files, position);
            return true;
        }

        string? text = data.GetData(DataFormats.UnicodeText) as string;

        // Spreadsheet cells (Excel, LibreOffice Calc): tab-separated text is the most faithful.
        if (text is not null && (data.GetDataPresent("XML Spreadsheet") || data.GetDataPresent("Csv")))
        {
            string? table = MarkdownTable.Build(MarkdownTable.ParseDelimited(text.TrimEnd('\r', '\n'), '\t'));
            if (table is not null)
            {
                InsertAt(position, table, block: true);
                return true;
            }
        }

        if (data.GetData(DataFormats.Html) is string cfHtml)
        {
            string html = ClipboardHtml.Extract(cfHtml);
            if (ClipboardHtml.IsFromDocumentApp(html) || ClipboardHtml.HasMarkdownWorthyStructure(html))
            {
                string markdown = HtmlToMarkdown.Convert(html, ResolvePastedImage).Trim('\n');
                if (markdown.Trim().Length > 0)
                {
                    bool block = markdown.Contains('\n') || markdown.StartsWith('#') || markdown.StartsWith('|');
                    InsertAt(position, markdown, block);
                    return true;
                }
            }
        }

        if (text is not null)
        {
            if (MarkdownTable.LooksLikeTsv(text) && MarkdownTable.Build(MarkdownTable.ParseDelimited(text.TrimEnd('\r', '\n'), '\t')) is string tsvTable)
            {
                InsertAt(position, tsvTable, block: true);
                return true;
            }
            if (position is int pos)
            {
                InsertAt(pos, text.Replace("\r\n", "\n"), block: false);
                return true;
            }
            return false;
        }

        string? imagePath = TryStageClipboardImage(data);
        if (imagePath is not null)
        {
            InsertAt(position, "![](" + DocumentAssets.ToLinkDestination(imagePath) + ")", block: false);
            return true;
        }

        return false;
    }

    /// <summary>Screenshots and "copy image" from browsers. The "PNG" format keeps
    /// transparency, so it's preferred over the plain bitmap when both are offered.</summary>
    private string? TryStageClipboardImage(IDataObject data)
    {
        try
        {
            if (data.GetData("PNG") is MemoryStream png)
                return _assets.AddImageBytes(png.ToArray(), ".png");
            if (data.GetData(DataFormats.Bitmap) is Image bitmap)
                return _assets.AddImage(bitmap);
        }
        catch (Exception ex) when (ex is IOException or ExternalException or ArgumentException)
        {
            MessageBox.Show(this, $"無法貼上圖片：\n{ex.Message}", "貼上失敗", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return null;
    }

    /// <summary>Images inside pasted HTML: Word points at temp files under msohtmlclip that
    /// vanish once the clipboard changes, Google Docs/browsers may inline data: URIs — both
    /// get staged so they're saved next to the document. Web URLs stay as they are.</summary>
    private string? ResolvePastedImage(string src)
    {
        try
        {
            if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                string local = new Uri(src).LocalPath;
                return File.Exists(local) ? DocumentAssets.ToMarkdownPath(_assets.AddTransientFile(local)) : null;
            }

            if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                int comma = src.IndexOf(',');
                if (comma < 0) return null;
                string header = src[..comma];
                if (!header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return null;
                string mime = header["data:image/".Length..header.IndexOf(';')];
                string ext = mime switch { "jpeg" => ".jpg", "svg+xml" => ".svg", _ => "." + mime };
                return _assets.AddImageBytes(Convert.FromBase64String(src[(comma + 1)..]), ext);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or UriFormatException or ArgumentException)
        {
            // Leave the original src — better a broken image link than losing the paste.
        }
        return null;
    }

    // ----- Files (drop / paste of copied files / menu) -----

    private void InsertFiles(IReadOnlyList<string> paths, int? position)
    {
        var parts = new List<(string Text, bool Block)>();
        foreach (string path in paths)
        {
            if (Directory.Exists(path)) continue;
            try
            {
                parts.Add(BuildMarkdownForFile(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                           or NotSupportedException or ExcelDataReader.Exceptions.ExcelReaderException)
            {
                MessageBox.Show(this, $"無法讀取「{Path.GetFileName(path)}」：\n{ex.Message}", "插入失敗",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        if (parts.Count == 0) return;

        bool block = parts.Any(p => p.Block);
        string text = string.Join(block ? "\n\n" : "\n", parts.Select(p => p.Text));
        InsertAt(position, text, block);
    }

    private (string Text, bool Block) BuildMarkdownForFile(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        string name = Path.GetFileNameWithoutExtension(path);

        if (DocumentAssets.IsImage(path))
        {
            string rel = _assets.AddFile(path);
            return ("![" + EscapeLinkText(name) + "](" + DocumentAssets.ToLinkDestination(rel) + ")", false);
        }

        if (MermaidExtensions.Contains(ext))
        {
            string source = File.ReadAllText(path).Replace("\r\n", "\n").Trim('\n');
            return ("```mermaid\n" + source + "\n```", true);
        }

        if (SpreadsheetReader.IsSupported(path))
        {
            var sheets = SpreadsheetReader.Read(path)
                .Select(s => (s.Name, Table: MarkdownTable.Build(s.Rows)))
                .Where(s => s.Table is not null)
                .ToList();
            if (sheets.Count == 0) throw new InvalidDataException("檔案中沒有任何資料。");
            if (sheets.Count == 1) return (sheets[0].Table!.TrimEnd('\n'), true);

            var sb = new StringBuilder();
            foreach (var (sheetName, table) in sheets)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("**").Append(EscapeLinkText(sheetName)).Append("**\n\n").Append(table!.TrimEnd('\n'));
            }
            return (sb.ToString(), true);
        }

        // Anything else becomes an attachment link, copied next to the document on save.
        string attachment = _assets.AddFile(path);
        return ("[" + EscapeLinkText(Path.GetFileName(path)) + "](" + DocumentAssets.ToLinkDestination(attachment) + ")", false);
    }

    private static string EscapeLinkText(string text) => text.Replace("[", "\\[").Replace("]", "\\]").Replace("*", "\\*");

    private void InsertFilesFromDialog(string title, string filter)
    {
        using var dialog = new OpenFileDialog { Title = title, Filter = filter, Multiselect = true };
        if (dialog.ShowDialog(this) == DialogResult.OK) InsertFiles(dialog.FileNames, position: null);
    }

    // ----- Text insertion -----

    private void InsertAt(int? position, string text, bool block)
    {
        if (position is int pos)
        {
            _editor.SetSelection(pos, pos);
        }
        ReplaceSelectionWith(text, block);
    }

    /// <summary>Replaces the selection with <paramref name="text"/> as one undo step. Block
    /// content (tables, fences, headings) gets blank lines around it so it doesn't merge
    /// into the neighbouring paragraph.</summary>
    private void ReplaceSelectionWith(string text, bool block)
    {
        int start = _editor.SelectionStart;
        int end = _editor.SelectionEnd;

        if (block)
        {
            string before = start > 0 ? _editor.GetTextRange(Math.Max(0, start - 2), Math.Min(2, start)) : string.Empty;
            string after = end < _editor.TextLength ? _editor.GetTextRange(end, Math.Min(2, _editor.TextLength - end)) : string.Empty;

            if (start > 0 && !before.EndsWith("\n\n", StringComparison.Ordinal))
                text = (before.EndsWith('\n') ? "\n" : "\n\n") + text;
            if (!after.StartsWith("\n\n", StringComparison.Ordinal))
                text += after.StartsWith('\n') ? "\n" : after.Length == 0 ? "\n" : "\n\n";
        }

        _editor.BeginUndoAction();
        try
        {
            _editor.DeleteRange(start, end - start);
            _editor.InsertText(start, text);
            _editor.GotoPosition(start + text.Length);
        }
        finally
        {
            _editor.EndUndoAction();
        }
        _editor.Focus();
    }

    // ----- Drag & drop onto the editor -----

    /// <summary>
    /// Scintilla registers its own OLE drop target, which only understands text — files
    /// dropped on it were simply refused. Swap it for WinForms' drop target so file drops
    /// can be handled here; ordinary text drags (including moving a selection within the
    /// editor) are reimplemented in <see cref="Editor_DragDrop"/>.
    /// </summary>
    private void TakeOverEditorDropTarget()
    {
        _editor.AllowDrop = false;
        RevokeDragDrop(_editor.Handle);
        _editor.AllowDrop = true;
    }

    private void Editor_DragEnter(object? sender, DragEventArgs e)
    {
        _dragIsInternalMove = false;
        if (e.Data is null) { e.Effect = DragDropEffects.None; return; }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effect = DragDropEffects.Copy;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Html))
        {
            string selected = _editor.SelectedText;
            _dragIsInternalMove = selected.Length > 0
                && (e.AllowedEffect & DragDropEffects.Move) != 0
                && e.Data.GetData(DataFormats.UnicodeText) as string == selected;
            Editor_DragOver(sender, e);
            return;
        }

        e.Effect = DragDropEffects.None;
    }

    private void Editor_DragOver(object? sender, DragEventArgs e)
    {
        if (e.Data is null) return;
        bool ctrl = (e.KeyState & 8) != 0;

        if (_dragIsInternalMove)
        {
            e.Effect = ctrl ? DragDropEffects.Copy : DragDropEffects.Move;
            return;
        }

        e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Html)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        // No selection to protect for outside drags — move the caret so the user can see
        // where the content will land.
        if (e.Effect != DragDropEffects.None)
        {
            int pos = DropPosition(e);
            if (pos >= 0 && pos != _editor.CurrentPosition) _editor.GotoPosition(pos);
        }
    }

    private void Editor_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data is null) return;
        int pos = DropPosition(e);
        if (pos < 0) pos = _editor.CurrentPosition;

        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
        {
            if (files.Length == 1 && MarkdownExtensions.Contains(Path.GetExtension(files[0]).ToLowerInvariant()))
            {
                if (ConfirmDiscardChanges()) LoadFile(files[0]);
                return;
            }
            Activate();
            InsertFiles(files, pos);
            return;
        }

        if (_dragIsInternalMove)
        {
            MoveSelectionTo(pos, copy: (e.KeyState & 8) != 0);
            // Report Copy even for a move: Scintilla, as the drag source, deletes the original
            // selection itself on a Move result — but the move has already been done here.
            e.Effect = DragDropEffects.Copy;
            _dragIsInternalMove = false;
            return;
        }

        InsertFromDataObject(e.Data, pos);
    }

    private int DropPosition(DragEventArgs e)
    {
        var client = _editor.PointToClient(new Point(e.X, e.Y));
        return _editor.CharPositionFromPoint(client.X, client.Y);
    }

    private void MoveSelectionTo(int pos, bool copy)
    {
        int start = _editor.SelectionStart;
        int end = _editor.SelectionEnd;
        if (pos >= start && pos <= end) return;

        string text = _editor.GetTextRange(start, end - start);
        _editor.BeginUndoAction();
        try
        {
            if (copy)
            {
                _editor.InsertText(pos, text);
            }
            else if (pos > end)
            {
                _editor.InsertText(pos, text);
                _editor.DeleteRange(start, end - start);
                pos -= text.Length;
            }
            else
            {
                _editor.DeleteRange(start, end - start);
                _editor.InsertText(pos, text);
            }
            _editor.SetSelection(pos + text.Length, pos);
        }
        finally
        {
            _editor.EndUndoAction();
        }
    }
}
