using System.Text.RegularExpressions;
using ScintillaNET;

namespace MarkForge.Editor;

public partial class MainForm
{
    private static readonly Regex LeadingWhitespaceRegex = new(@"^[ \t]*", RegexOptions.Compiled);
    private static readonly Regex OrderedListRegex = new(@"^(?<indent>[ \t]*)(?<num>\d+)(?<delim>[.)])(?<space>\s+)", RegexOptions.Compiled);
    private static readonly Regex UnorderedListRegex = new(@"^(?<indent>[ \t]*)(?<marker>[-*+])(?<space>\s+)(?<checkbox>\[[ xX]\]\s+)?", RegexOptions.Compiled);
    private static readonly Regex BlockquoteRegex = new(@"^(?<indent>[ \t]*)(?<marks>>+)\s*", RegexOptions.Compiled);
    private static readonly Regex TaskMarkerRegex = new(@"^(?<indent>[ \t]*)[-*+]\s+\[(?<mark>.)\]\s+", RegexOptions.Compiled);

    // Styles the lexer actually paints onto visible text (everything except headers,
    // which get their own progressively larger size below).
    private static readonly int[] EditorBodyStyleIds =
    {
        Style.Default,
        Style.Markdown.Default,
        Style.Markdown.LineBegin,
        Style.Markdown.Strong1, Style.Markdown.Strong2,
        Style.Markdown.Em1, Style.Markdown.Em2,
        Style.Markdown.PreChar,
        Style.Markdown.UListItem, Style.Markdown.OListItem,
        Style.Markdown.BlockQuote,
        Style.Markdown.Strikeout,
        Style.Markdown.HRule,
        Style.Markdown.Link,
        Style.Markdown.Code, Style.Markdown.Code2, Style.Markdown.CodeBk,
    };

    private Scintilla BuildEditor()
    {
        var editor = new Scintilla
        {
            Dock = DockStyle.Fill,
            LexerName = "markdown",
            WrapMode = WrapMode.Word,
            IndentWidth = 4,
            TabWidth = 4,
            UseTabs = false,
            BorderStyle = ScintillaNET.BorderStyle.None,
        };

        editor.Margins[0].Type = MarginType.Number;
        editor.Margins[0].Width = 46;
        editor.Margins[1].Width = 4;

        // StyleClearAll must run exactly once, before any per-style customization below —
        // it resets every style to mirror Style.Default, so calling it later would wipe
        // out the colors/fonts ApplyEditorAppearance() sets afterward.
        editor.StyleClearAll();

        // Structural attributes only (bold/italic/underline). Colors, font family and size
        // are theme- and preference-dependent, so ApplyEditorAppearance() owns those and is
        // called right after this method returns, and again whenever theme/font changes.
        editor.Styles[Style.Markdown.Header1].Bold = true;
        editor.Styles[Style.Markdown.Header2].Bold = true;
        editor.Styles[Style.Markdown.Header3].Bold = true;
        editor.Styles[Style.Markdown.Header4].Bold = true;
        editor.Styles[Style.Markdown.Header5].Bold = true;
        editor.Styles[Style.Markdown.Header6].Bold = true;
        editor.Styles[Style.Markdown.Strong1].Bold = true;
        editor.Styles[Style.Markdown.Strong2].Bold = true;
        editor.Styles[Style.Markdown.Em1].Italic = true;
        editor.Styles[Style.Markdown.Em2].Italic = true;
        editor.Styles[Style.Markdown.BlockQuote].Italic = true;
        editor.Styles[Style.Markdown.Link].Underline = true;

        editor.CharAdded += Editor_CharAdded;
        editor.Insert += (_, _) => OnEditorChanged();
        editor.Delete += (_, _) => OnEditorChanged();
        editor.UpdateUI += (_, _) => UpdateStatusBar();
        editor.UpdateUI += (_, e) =>
        {
            if ((e.Change & UpdateChange.VScroll) == 0) return;
            if (_suppressEditorScrollSync)
            {
                // Still churning from a load — push the settle window back out rather than
                // just dropping this one; see BeginScrollSyncSettleWindow.
                _scrollSettleTimer.Stop();
                _scrollSettleTimer.Start();
                return;
            }
            SyncPreviewScrollFromEditor();
        };

        return editor;
    }

    /// <summary>
    /// Applies the current theme (light/dark) and the chosen editor font/size to every
    /// lexer style. Must be called after any theme or font change — the Scintilla markdown
    /// lexer paints text using its own numbered styles (Style.Markdown.*), not Style.Default,
    /// so all of them need explicit colors or they silently keep stale/default ones.
    /// </summary>
    private void ApplyEditorAppearance()
    {
        Color bg, text, muted, listColor, linkColor, codeBg, lineNoFg;
        if (_darkTheme)
        {
            bg = Color.FromArgb(0x0D, 0x11, 0x17);
            text = Color.FromArgb(0xE6, 0xED, 0xF3);
            muted = Color.FromArgb(0x91, 0x98, 0xA1);
            listColor = Color.FromArgb(0xE3, 0xB3, 0x41);
            linkColor = Color.FromArgb(0x44, 0x93, 0xF8);
            codeBg = Color.FromArgb(0x16, 0x1B, 0x22);
            lineNoFg = Color.FromArgb(0x6E, 0x76, 0x81);
        }
        else
        {
            bg = Color.White;
            text = Color.FromArgb(0x1F, 0x23, 0x28);
            muted = Color.FromArgb(0x59, 0x63, 0x6E);
            listColor = Color.FromArgb(0x9A, 0x62, 0x00);
            linkColor = Color.FromArgb(0x09, 0x69, 0xDA);
            codeBg = Color.FromArgb(0xF6, 0xF8, 0xFA);
            lineNoFg = Color.FromArgb(0x8B, 0x94, 0x9E);
        }

        float baseSize = _editorFontSize;
        (int id, float size)[] headerSizes =
        {
            (Style.Markdown.Header1, baseSize + 9),
            (Style.Markdown.Header2, baseSize + 7),
            (Style.Markdown.Header3, baseSize + 5),
            (Style.Markdown.Header4, baseSize + 3),
            (Style.Markdown.Header5, baseSize + 1),
            (Style.Markdown.Header6, baseSize),
        };

        foreach (int id in EditorBodyStyleIds)
        {
            var style = _editor.Styles[id];
            style.Font = _editorFontFamily;
            style.SizeF = baseSize;
            style.ForeColor = text;
            style.BackColor = bg;
        }

        foreach (var (id, size) in headerSizes)
        {
            var style = _editor.Styles[id];
            style.Font = _editorFontFamily;
            style.SizeF = size;
            style.ForeColor = text;
            style.BackColor = bg;
        }

        _editor.Styles[Style.Markdown.Strikeout].ForeColor = muted;
        _editor.Styles[Style.Markdown.UListItem].ForeColor = listColor;
        _editor.Styles[Style.Markdown.OListItem].ForeColor = listColor;
        _editor.Styles[Style.Markdown.BlockQuote].ForeColor = muted;
        _editor.Styles[Style.Markdown.HRule].ForeColor = muted;
        _editor.Styles[Style.Markdown.PreChar].ForeColor = muted;
        _editor.Styles[Style.Markdown.Link].ForeColor = linkColor;
        _editor.Styles[Style.Markdown.Code].BackColor = codeBg;
        _editor.Styles[Style.Markdown.Code2].BackColor = codeBg;
        _editor.Styles[Style.Markdown.CodeBk].BackColor = codeBg;

        _editor.Styles[Style.LineNumber].Font = _editorFontFamily;
        _editor.Styles[Style.LineNumber].SizeF = baseSize;
        _editor.Styles[Style.LineNumber].ForeColor = lineNoFg;
        _editor.Styles[Style.LineNumber].BackColor = bg;

        _editor.Margins[0].BackColor = bg;
        _editor.Margins[1].BackColor = bg;
        _editor.CaretForeColor = _darkTheme ? Color.White : Color.Black;
    }

    private void ChooseEditorFont()
    {
        using var dialog = new FontDialog
        {
            Font = new Font(_editorFontFamily, _editorFontSize),
            ShowEffects = false,
            FontMustExist = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        _editorFontFamily = dialog.Font.Name;
        _editorFontSize = dialog.Font.Size;
        ApplyEditorAppearance();
    }

    // ----- Auto-indent -----

    private void Editor_CharAdded(object? sender, CharAddedEventArgs e)
    {
        if (e.Char != '\n') return;

        int lineIndex = _editor.CurrentLine;
        if (lineIndex <= 0) return;

        string prevText = _editor.Lines[lineIndex - 1].Text.TrimEnd('\r', '\n');
        string insertText = BuildContinuation(prevText);
        if (string.IsNullOrEmpty(insertText)) return;

        int pos = _editor.CurrentPosition;
        _editor.InsertText(pos, insertText);
        _editor.GotoPosition(pos + insertText.Length);
    }

    private static string BuildContinuation(string prevText)
    {
        var ordered = OrderedListRegex.Match(prevText);
        if (ordered.Success)
        {
            int num = int.TryParse(ordered.Groups["num"].Value, out int n) ? n : 0;
            return $"{ordered.Groups["indent"].Value}{num + 1}{ordered.Groups["delim"].Value}{ordered.Groups["space"].Value}";
        }

        var unordered = UnorderedListRegex.Match(prevText);
        if (unordered.Success)
        {
            string checkbox = unordered.Groups["checkbox"].Success ? "[ ] " : string.Empty;
            return $"{unordered.Groups["indent"].Value}{unordered.Groups["marker"].Value}{unordered.Groups["space"].Value}{checkbox}";
        }

        var blockquote = BlockquoteRegex.Match(prevText);
        if (blockquote.Success && prevText.Trim().Length > blockquote.Value.Trim().Length)
        {
            return $"{blockquote.Groups["indent"].Value}{blockquote.Groups["marks"].Value} ";
        }

        return LeadingWhitespaceRegex.Match(prevText).Value;
    }

    // ----- Formatting commands -----

    private void ActionBold() => ToggleWrapSelection("**");
    private void ActionItalic() => ToggleWrapSelection("*");
    private void ActionStrikethrough() => ToggleWrapSelection("~~");
    private void ActionInlineCode() => ToggleWrapSelection("`");

    private void ActionHeading(int level)
    {
        level = Math.Clamp(level, 1, 6);
        int lineIdx = _editor.CurrentLine;
        var line = _editor.Lines[lineIdx];
        string text = line.Text.TrimEnd('\r', '\n');
        var match = Regex.Match(text, @"^(#{1,6})(\s+)");
        string newPrefix = new string('#', level) + " ";

        _editor.BeginUndoAction();
        try
        {
            if (match.Success)
            {
                if (match.Groups[1].Value.Length == level)
                {
                    _editor.DeleteRange(line.Position, match.Length);
                }
                else
                {
                    _editor.DeleteRange(line.Position, match.Length);
                    _editor.InsertText(line.Position, newPrefix);
                }
            }
            else
            {
                _editor.InsertText(line.Position, newPrefix);
            }
        }
        finally
        {
            _editor.EndUndoAction();
        }
        _editor.Focus();
    }

    private void ActionBlockquote() => ApplyLinePrefixToggle("> ");
    private void ActionBulletList() => ApplyLinePrefixToggle("- ");
    private void ActionTaskList() => ApplyLinePrefixToggle("- [ ] ");

    // "x" keeps GFM's actual checked-checkbox syntax so the preview renders a real ☑;
    // the other three are plain symbols (not full-color emoji) so they render reliably
    // inside the Scintilla editor's own text — the toolbar buttons show the colored emoji.
    private void ActionTaskDone() => SetTaskMarker("x");
    private void ActionTaskCancelled() => SetTaskMarker("✗");
    private void ActionTaskPending() => SetTaskMarker("▲");
    private void ActionTaskImportant() => SetTaskMarker("!");

    /// <summary>
    /// Sets (or adds) the "- [x] " task marker on every selected line, replacing just the
    /// character inside the brackets if a marker is already there so the rest of the line's
    /// content is preserved.
    /// </summary>
    private void SetTaskMarker(string symbol)
    {
        int selStart = _editor.SelectionStart;
        int selEnd = _editor.SelectionEnd;
        int firstLine = _editor.LineFromPosition(selStart);
        int lastLine = _editor.LineFromPosition(selEnd);

        _editor.BeginUndoAction();
        try
        {
            for (int i = lastLine; i >= firstLine; i--)
            {
                var line = _editor.Lines[i];
                string text = line.Text;
                var match = TaskMarkerRegex.Match(text);

                if (match.Success)
                {
                    var mark = match.Groups["mark"];
                    _editor.DeleteRange(line.Position + mark.Index, mark.Length);
                    _editor.InsertText(line.Position + mark.Index, symbol);
                }
                else
                {
                    string trimmed = text.TrimStart(' ', '\t');
                    int indentLen = text.Length - trimmed.Length;
                    _editor.InsertText(line.Position + indentLen, $"- [{symbol}] ");
                }
            }
        }
        finally
        {
            _editor.EndUndoAction();
        }
        MoveCaretToLineEnd(lastLine);
    }

    private void ActionOrderedList()
    {
        int selStart = _editor.SelectionStart;
        int selEnd = _editor.SelectionEnd;
        int firstLine = _editor.LineFromPosition(selStart);
        int lastLine = _editor.LineFromPosition(selEnd);

        bool allNumbered = true;
        for (int i = firstLine; i <= lastLine; i++)
        {
            if (!OrderedListRegex.IsMatch(_editor.Lines[i].Text))
            {
                allNumbered = false;
                break;
            }
        }

        _editor.BeginUndoAction();
        try
        {
            if (allNumbered)
            {
                for (int i = lastLine; i >= firstLine; i--)
                {
                    var line = _editor.Lines[i];
                    var m = OrderedListRegex.Match(line.Text);
                    if (m.Success) _editor.DeleteRange(line.Position, m.Length);
                }
            }
            else
            {
                int number = 1;
                for (int i = firstLine; i <= lastLine; i++)
                {
                    var line = _editor.Lines[i];
                    string prefix = $"{number}. ";
                    _editor.InsertText(line.Position, prefix);
                    number++;
                    // Re-fetch lines each iteration since positions shift as we insert top-down;
                    // recompute using the live Lines collection on next loop iteration.
                }
            }
        }
        finally
        {
            _editor.EndUndoAction();
        }
        MoveCaretToLineEnd(lastLine);
    }

    /// <summary>
    /// Places the caret at the end of the given line and refocuses the editor, so clicking a
    /// list/quote toolbar button lets the user start typing content immediately afterward
    /// instead of having to click back into the editor and reposition manually.
    /// </summary>
    private void MoveCaretToLineEnd(int lineIndex)
    {
        lineIndex = Math.Clamp(lineIndex, 0, _editor.Lines.Count - 1);
        int pos = _editor.Lines[lineIndex].EndPosition;
        _editor.GotoPosition(pos);
        _editor.Focus();
    }

    private void ApplyLinePrefixToggle(string prefix)
    {
        int selStart = _editor.SelectionStart;
        int selEnd = _editor.SelectionEnd;
        int firstLine = _editor.LineFromPosition(selStart);
        int lastLine = _editor.LineFromPosition(selEnd);

        bool allHavePrefix = true;
        for (int i = firstLine; i <= lastLine; i++)
        {
            if (!_editor.Lines[i].Text.TrimStart(' ', '\t').StartsWith(prefix, StringComparison.Ordinal))
            {
                allHavePrefix = false;
                break;
            }
        }

        _editor.BeginUndoAction();
        try
        {
            for (int i = lastLine; i >= firstLine; i--)
            {
                var line = _editor.Lines[i];
                string text = line.Text;
                string trimmed = text.TrimStart(' ', '\t');
                int indentLen = text.Length - trimmed.Length;
                int pos = line.Position + indentLen;

                if (allHavePrefix)
                {
                    if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                        _editor.DeleteRange(pos, prefix.Length);
                }
                else
                {
                    if (!trimmed.StartsWith(prefix, StringComparison.Ordinal))
                        _editor.InsertText(pos, prefix);
                }
            }
        }
        finally
        {
            _editor.EndUndoAction();
        }
        MoveCaretToLineEnd(lastLine);
    }

    private void ActionCodeBlock()
    {
        int start = _editor.SelectionStart;
        int end = _editor.SelectionEnd;
        string selected = _editor.GetTextRange(start, end - start);

        string block = selected.Length > 0
            ? $"```\n{selected}\n```"
            : "```\n\n```";

        _editor.BeginUndoAction();
        try
        {
            _editor.DeleteRange(start, selected.Length);
            _editor.InsertText(start, block);
            int caret = selected.Length > 0 ? start + block.Length : start + 4;
            _editor.SetSelection(caret, caret);
        }
        finally
        {
            _editor.EndUndoAction();
        }
        _editor.Focus();
    }

    private void ActionLink() => InsertLinkLikeTemplate(
        openMarker: "[", textPlaceholder: "連結文字", midMarker: "](", urlPlaceholder: "https://", closeMarker: ")");

    private void ActionImage() => InsertLinkLikeTemplate(
        openMarker: "![", textPlaceholder: "替代文字", midMarker: "](", urlPlaceholder: "image-url", closeMarker: ")");

    /// <summary>
    /// Shared implementation for [text](url) and ![alt](url) templates: with a selection,
    /// the selected text becomes the label and the URL placeholder is pre-selected for typing;
    /// with no selection, the text placeholder is pre-selected instead.
    /// </summary>
    private void InsertLinkLikeTemplate(string openMarker, string textPlaceholder, string midMarker, string urlPlaceholder, string closeMarker)
    {
        int start = _editor.SelectionStart;
        int end = _editor.SelectionEnd;
        string selected = _editor.GetTextRange(start, end - start);
        string label = selected.Length > 0 ? selected : textPlaceholder;
        string replacement = openMarker + label + midMarker + urlPlaceholder + closeMarker;

        _editor.BeginUndoAction();
        try
        {
            _editor.DeleteRange(start, selected.Length);
            _editor.InsertText(start, replacement);

            if (selected.Length > 0)
            {
                int urlStart = start + openMarker.Length + label.Length + midMarker.Length;
                _editor.SetSelection(urlStart, urlStart + urlPlaceholder.Length);
            }
            else
            {
                int labelStart = start + openMarker.Length;
                _editor.SetSelection(labelStart, labelStart + label.Length);
            }
        }
        finally
        {
            _editor.EndUndoAction();
        }
        _editor.Focus();
    }

    private void ActionTable()
    {
        const string table =
            "| 欄位 1 | 欄位 2 | 欄位 3 |\n" +
            "| --- | --- | --- |\n" +
            "| 內容 | 內容 | 內容 |\n" +
            "| 內容 | 內容 | 內容 |\n";

        int pos = _editor.CurrentPosition;
        bool needsLeadingNewline = pos > 0 && _editor.GetTextRange(pos - 1, 1) != "\n";
        string insert = (needsLeadingNewline ? "\n" : string.Empty) + table;

        _editor.InsertText(pos, insert);
        _editor.GotoPosition(pos + insert.Length);
        _editor.Focus();
    }

    private void ActionHorizontalRule()
    {
        int pos = _editor.CurrentPosition;
        bool needsLeadingNewline = pos > 0 && _editor.GetTextRange(pos - 1, 1) != "\n";
        string insert = (needsLeadingNewline ? "\n\n" : "\n") + "---\n\n";
        _editor.InsertText(pos, insert);
        _editor.GotoPosition(pos + insert.Length);
        _editor.Focus();
    }

    private void ToggleWrapSelection(string marker)
    {
        int start = _editor.SelectionStart;
        int end = _editor.SelectionEnd;
        string selected = _editor.GetTextRange(start, end - start);

        _editor.BeginUndoAction();
        try
        {
            if (selected.Length >= marker.Length * 2 &&
                selected.StartsWith(marker, StringComparison.Ordinal) &&
                selected.EndsWith(marker, StringComparison.Ordinal))
            {
                string inner = selected.Substring(marker.Length, selected.Length - marker.Length * 2);
                _editor.DeleteRange(start, selected.Length);
                _editor.InsertText(start, inner);
                _editor.SetSelection(start, start + inner.Length);
            }
            else if (IsSurroundedBy(start, end, marker))
            {
                int outerStart = start - marker.Length;
                int outerEnd = end + marker.Length;
                _editor.DeleteRange(end, marker.Length);
                _editor.DeleteRange(outerStart, marker.Length);
                _editor.SetSelection(outerStart, outerStart + selected.Length);
            }
            else
            {
                string replacement = marker + selected + marker;
                _editor.DeleteRange(start, selected.Length);
                _editor.InsertText(start, replacement);
                if (selected.Length == 0)
                    _editor.SetSelection(start + marker.Length, start + marker.Length);
                else
                    _editor.SetSelection(start, start + replacement.Length);
            }
        }
        finally
        {
            _editor.EndUndoAction();
        }
        _editor.Focus();
    }

    private bool IsSurroundedBy(int start, int end, string marker)
    {
        if (start < marker.Length) return false;
        if (end + marker.Length > _editor.TextLength) return false;
        string before = _editor.GetTextRange(start - marker.Length, marker.Length);
        string after = _editor.GetTextRange(end, marker.Length);
        return before == marker && after == marker;
    }

    // ----- Status / change tracking -----

    private void OnEditorChanged()
    {
        if (!_suppressDirty)
        {
            _isDirty = true;
            UpdateTitle();
        }
        SchedulePreviewUpdate();
    }

    private void UpdateStatusBar()
    {
        int lineIdx = _editor.CurrentLine;
        int col = _editor.CurrentPosition - _editor.Lines[lineIdx].Position + 1;
        _statusPosition.Text = $"行 {lineIdx + 1}，列 {col}";

        string text = _editor.Text;
        int wordCount = Regex.Matches(text, @"[\p{L}\p{N}]+").Count;
        _statusWordCount.Text = $"{wordCount} 字 / {text.Length} 字元";
    }
}
