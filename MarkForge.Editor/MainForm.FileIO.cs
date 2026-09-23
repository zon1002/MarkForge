namespace MarkForge.Editor;

public partial class MainForm
{
    private const string MarkdownFilter = "Markdown 檔案 (*.md;*.markdown)|*.md;*.markdown|所有檔案 (*.*)|*.*";

    private void NewDocument()
    {
        if (!ConfirmDiscardChanges()) return;

        _suppressDirty = true;
        _editor.Text = "# 新文件\n\n開始輸入內容...\n";
        _suppressDirty = false;
        // A full-text assignment leaves the caret at end-of-document, and Scintilla scrolls
        // to follow it — without this a new/opened document would visibly open scrolled to
        // its bottom, and (now that the preview follows the editor's scroll) drag the preview
        // down with it too.
        _editor.GotoPosition(0);
        BeginScrollSyncSettleWindow();

        _currentFilePath = null;
        _assets.Reset(null);
        _isDirty = false;
        UpdateTitle();
        SchedulePreviewUpdate();
    }

    private void OpenDocument()
    {
        if (!ConfirmDiscardChanges()) return;

        using var dialog = new OpenFileDialog { Filter = MarkdownFilter };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            LoadFile(dialog.FileName);
        }
    }

    private void LoadFile(string path)
    {
        try
        {
            string content = File.ReadAllText(path);
            _suppressDirty = true;
            _editor.Text = content;
            _suppressDirty = false;
            _editor.GotoPosition(0);
            BeginScrollSyncSettleWindow();

            _currentFilePath = path;
            _assets.Reset(path);
            _isDirty = false;
            UpdateTitle();
            SchedulePreviewUpdate();
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"無法開啟檔案：\n{ex.Message}", "開啟失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, $"無法開啟檔案：\n{ex.Message}", "開啟失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool SaveDocument()
    {
        if (_currentFilePath is null) return SaveDocumentAs();

        try
        {
            // Copy dragged/pasted images and attachments next to the .md first; a name clash
            // with an unrelated file there renames the copy, and the text is updated to match.
            var (markdown, failures) = _assets.CommitForSave(_editor.Text, _currentFilePath);
            if (markdown != _editor.Text) ReplaceTextPreservingView(markdown);

            File.WriteAllText(_currentFilePath, markdown);
            _isDirty = false;
            UpdateTitle();
            SchedulePreviewUpdate();

            if (failures.Count > 0)
            {
                MessageBox.Show(this, "文件已儲存，但下列附加檔案無法複製到文件資料夾：\n\n" + string.Join("\n", failures),
                    "部分檔案未複製", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return true;
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"無法儲存檔案：\n{ex.Message}", "儲存失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            MessageBox.Show(this, $"無法儲存檔案：\n{ex.Message}", "儲存失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// <summary>Swaps in rewritten text (renamed attachments) as one undo step, keeping the
    /// caret and scroll position where they were.</summary>
    private void ReplaceTextPreservingView(string text)
    {
        int caret = Math.Min(_editor.CurrentPosition, text.Length);
        int firstLine = _editor.FirstVisibleLine;
        _editor.BeginUndoAction();
        try
        {
            _editor.TargetWholeDocument();
            _editor.ReplaceTarget(text);
        }
        finally
        {
            _editor.EndUndoAction();
        }
        _editor.GotoPosition(caret);
        _editor.FirstVisibleLine = firstLine;
    }

    private bool SaveDocumentAs()
    {
        using var dialog = new SaveFileDialog { Filter = MarkdownFilter, FileName = _currentFilePath is null ? "未命名.md" : Path.GetFileName(_currentFilePath) };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;

        _currentFilePath = dialog.FileName;
        return SaveDocument();
    }

    private void ExportHtml()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "HTML 檔案 (*.html)|*.html|所有檔案 (*.*)|*.*",
            FileName = _currentFilePath is null ? "未命名.html" : Path.ChangeExtension(Path.GetFileName(_currentFilePath), ".html"),
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            string html = Core.MarkdownRenderer.RenderStandaloneDocument(_editor.Text, _darkTheme, _previewFontFamily, _previewFontSize, _previewWidthPx);
            File.WriteAllText(dialog.FileName, html);
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, $"無法匯出檔案：\n{ex.Message}", "匯出失敗", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async void ExportPdf()
    {
        if (_preview.CoreWebView2 is null)
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

        _previewDebounceTimer.Stop();
        await PushPreviewAsync();

        try
        {
            bool success = await _preview.CoreWebView2.PrintToPdfAsync(dialog.FileName);
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

    private bool ConfirmDiscardChanges()
    {
        if (!_isDirty) return true;

        var result = MessageBox.Show(
            this,
            "目前文件尚未儲存，是否要先儲存變更？",
            "未儲存的變更",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Warning);

        return result switch
        {
            DialogResult.Yes => SaveDocument(),
            DialogResult.No => true,
            _ => false,
        };
    }

    private void UpdateTitle()
    {
        string name = _currentFilePath is null ? "未命名" : Path.GetFileName(_currentFilePath);
        string dirtyMark = _isDirty ? "*" : string.Empty;
        Text = $"{dirtyMark}{name} - MarkForge";
        _statusFile.Text = _currentFilePath ?? "新檔案（尚未儲存）";
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!ConfirmDiscardChanges())
        {
            e.Cancel = true;
        }
    }

    private void MainForm_DragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    /// <summary>Drops on the window outside the editor (toolbar, status bar): a Markdown file
    /// opens as before; anything else is inserted at the caret like a drop on the editor.</summary>
    private void MainForm_DragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            if (paths.Length == 1 && MarkdownExtensions.Contains(Path.GetExtension(paths[0]).ToLowerInvariant()))
            {
                if (!ConfirmDiscardChanges()) return;
                LoadFile(paths[0]);
                return;
            }
            InsertFiles(paths, position: null);
        }
    }
}
