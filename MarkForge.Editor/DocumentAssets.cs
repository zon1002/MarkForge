using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MarkForge.Editor;

/// <summary>
/// Tracks files the document references but that don't live next to it yet — images and
/// attachments dragged in from elsewhere, pasted screenshots, images embedded in Word
/// content — and copies them into the Markdown file's folder when it's saved.
///
/// Until then each one is known by the relative name the Markdown already uses for it
/// ("photo.png"), mapped to where the bytes currently are (the original file, or a copy in
/// a per-session staging folder for clipboard data that has no file of its own). The live
/// preview resolves images through <see cref="Resolve"/>, so they show up immediately,
/// before the document has ever been saved.
/// </summary>
internal sealed class DocumentAssets : IDisposable
{
    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tif", ".tiff", ".avif" };

    private static readonly Regex MarkdownLinkRegex = new(
        @"!?\[(?:[^\]\\]|\\.)*\]\(\s*(?:<(?<url>[^>\r\n]+)>|(?<url>[^\s)]+))",
        RegexOptions.Compiled);
    private static readonly Regex HtmlSrcRegex = new(
        @"<(?:img|source|video|audio)\b[^>]*?\ssrc\s*=\s*[""'](?<url>[^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _stagingDir;
    private int _clipboardCounter;

    public DocumentAssets()
    {
        _stagingDir = Path.Combine(Path.GetTempPath(), "MarkForge", "Editor", "Staging",
            Guid.NewGuid().ToString("N")[..12]);
    }

    /// <summary>Folder of the saved .md file, or null while the document is untitled.</summary>
    public string? DocumentDirectory { get; private set; }

    /// <summary>Forget pending files and switch to a different document (new / open).</summary>
    public void Reset(string? documentPath)
    {
        _pending.Clear();
        DocumentDirectory = documentPath is null ? null : Path.GetDirectoryName(Path.GetFullPath(documentPath));
    }

    public static bool IsImage(string path)
        => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>Registers an existing file and returns the relative path to reference it by.
    /// A file already inside the document's folder is referenced in place.</summary>
    public string AddFile(string sourcePath, string? preferredName = null)
    {
        string full = Path.GetFullPath(sourcePath);

        if (DocumentDirectory is not null)
        {
            string docDir = DocumentDirectory.TrimEnd('\\') + "\\";
            if (full.StartsWith(docDir, StringComparison.OrdinalIgnoreCase))
                return ToMarkdownPath(Path.GetRelativePath(DocumentDirectory, full));
        }

        string name = SanitizeFileName(preferredName ?? Path.GetFileName(full));
        string stem = Path.GetFileNameWithoutExtension(name);
        string ext = Path.GetExtension(name);

        for (int i = 1; ; i++)
        {
            string candidate = i == 1 ? name : $"{stem}-{i}{ext}";

            if (_pending.TryGetValue(candidate, out string? existing))
            {
                if (SamePath(existing, full) || FilesEqual(existing, full)) return candidate;
                continue;
            }

            if (DocumentDirectory is not null)
            {
                string target = Path.Combine(DocumentDirectory, candidate);
                if (File.Exists(target))
                {
                    if (FilesEqual(target, full)) return candidate; // already there, nothing to copy
                    continue;
                }
            }

            _pending[candidate] = full;
            return candidate;
        }
    }

    /// <summary>Copies a file that won't stay around (Word's msohtmlclip temp images) into
    /// staging first, then registers it under a readable name.</summary>
    public string AddTransientFile(string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath);
        string staged = NewStagingPath(ext);
        File.Copy(sourcePath, staged, overwrite: true);
        return AddFile(staged, Path.GetFileName(staged));
    }

    /// <summary>Stages raw image bytes (a data: URI, a PNG clipboard stream).</summary>
    public string AddImageBytes(byte[] bytes, string extension)
    {
        string staged = NewStagingPath(extension);
        File.WriteAllBytes(staged, bytes);
        return AddFile(staged, Path.GetFileName(staged));
    }

    /// <summary>Stages a clipboard bitmap as PNG.</summary>
    public string AddImage(Image image)
    {
        string staged = NewStagingPath(".png");
        image.Save(staged, ImageFormat.Png);
        return AddFile(staged, Path.GetFileName(staged));
    }

    /// <summary>Maps a relative path used in the Markdown to the file that currently holds
    /// it — a pending file first, then the document's own folder.</summary>
    public string? Resolve(string relativePath)
    {
        string rel = relativePath.Replace('\\', '/').TrimStart('/');
        if (rel.StartsWith("./", StringComparison.Ordinal)) rel = rel[2..];

        if (_pending.TryGetValue(rel, out string? pending) && File.Exists(pending)) return pending;
        if (DocumentDirectory is null) return null;

        string candidate = Path.GetFullPath(Path.Combine(DocumentDirectory, rel));
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Copies every relative file the Markdown references into <paramref name="documentPath"/>'s
    /// folder: pending (dragged/pasted) files, plus — on Save As to a different folder — files
    /// that sat next to the previous copy of the document. A name already taken by a different
    /// file gets a "-2" style suffix and the Markdown is rewritten to match.
    /// </summary>
    /// <returns>The (possibly rewritten) Markdown, and any files that couldn't be copied.</returns>
    public (string Markdown, List<string> Failures) CommitForSave(string markdown, string documentPath)
    {
        string targetDir = Path.GetDirectoryName(Path.GetFullPath(documentPath))!;
        string? previousDir = DocumentDirectory;
        var failures = new List<string>();
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string url in FindRelativeReferences(markdown))
        {
            string rel = NormalizeReference(url);
            if (rel.Length == 0) continue;

            string? source = null;
            if (_pending.TryGetValue(rel, out string? pending)) source = pending;
            else if (previousDir is not null && !SamePath(previousDir, targetDir))
            {
                string old = Path.GetFullPath(Path.Combine(previousDir, rel));
                if (File.Exists(old)) source = old;
            }
            if (source is null) continue;

            try
            {
                if (!File.Exists(source)) { failures.Add(rel); continue; }

                string target = Path.GetFullPath(Path.Combine(targetDir, rel));
                string finalRel = rel;
                if (SamePath(source, target)) { _pending.Remove(rel); continue; }

                if (File.Exists(target) && !FilesEqual(source, target))
                {
                    string dir = Path.GetDirectoryName(target)!;
                    string stem = Path.GetFileNameWithoutExtension(target);
                    string ext = Path.GetExtension(target);
                    for (int i = 2; ; i++)
                    {
                        target = Path.Combine(dir, $"{stem}-{i}{ext}");
                        if (!File.Exists(target) || FilesEqual(source, target)) break;
                    }
                    finalRel = ToMarkdownPath(Path.GetRelativePath(targetDir, target));
                    renames[url] = finalRel;
                }

                if (!File.Exists(target))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(source, target);
                }
                _pending.Remove(rel);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{rel}（{ex.Message}）");
            }
        }

        foreach (var (from, to) in renames)
            markdown = ReplaceReference(markdown, from, to);

        DocumentDirectory = targetDir;
        return (markdown, failures);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_stagingDir)) Directory.Delete(_stagingDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort — it's under %TEMP%.
        }
    }

    // ----- helpers -----

    private string NewStagingPath(string extension)
    {
        Directory.CreateDirectory(_stagingDir);
        string ext = string.IsNullOrEmpty(extension) ? ".png" : extension.ToLowerInvariant();
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string path;
        do
        {
            _clipboardCounter++;
            path = Path.Combine(_stagingDir, $"image-{stamp}-{_clipboardCounter}{ext}");
        } while (File.Exists(path));
        return path;
    }

    private static IEnumerable<string> FindRelativeReferences(string markdown)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in MarkdownLinkRegex.Matches(markdown).Concat(HtmlSrcRegex.Matches(markdown)))
        {
            string url = m.Groups["url"].Value.Trim();
            if (IsRelative(url) && seen.Add(url)) yield return url;
        }
    }

    private static bool IsRelative(string url)
    {
        if (url.Length == 0 || url.StartsWith('#') || url.StartsWith('/') || url.StartsWith('\\')) return false;
        return !Regex.IsMatch(url, @"^[a-zA-Z][a-zA-Z0-9+.-]*:");
    }

    private static string NormalizeReference(string url)
    {
        int cut = url.IndexOfAny(new[] { '#', '?' });
        if (cut >= 0) url = url[..cut];
        try { url = Uri.UnescapeDataString(url); } catch (UriFormatException) { }
        url = url.Replace('\\', '/');
        if (url.StartsWith("./", StringComparison.Ordinal)) url = url[2..];
        return url;
    }

    private static string ReplaceReference(string markdown, string from, string to)
    {
        string toUrl = to.Contains(' ') ? to.Replace(" ", "%20") : to;
        return Regex.Replace(markdown,
            @"(?<=[(<""'])" + Regex.Escape(from) + @"(?=[)>""'\s])",
            toUrl.Replace("$", "$$"));
    }

    public static string ToMarkdownPath(string relativePath) => relativePath.Replace('\\', '/');

    /// <summary>Markdown URL for a relative path: spaces and parentheses would end a plain
    /// link destination early, so such paths go in angle brackets.</summary>
    public static string ToLinkDestination(string relativePath)
        => relativePath.IndexOfAny(new[] { ' ', '(', ')' }) >= 0 ? "<" + relativePath + ">" : relativePath;

    private static string SanitizeFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Length == 0 ? "file" : name;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    private static bool FilesEqual(string a, string b)
    {
        try
        {
            var fa = new FileInfo(a);
            var fb = new FileInfo(b);
            if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
            if (fa.Length > 64 * 1024 * 1024) return fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;

            using var sa = fa.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sb = fb.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var bufA = new byte[81920];
            var bufB = new byte[81920];
            while (true)
            {
                int na = sa.ReadAtLeast(bufA, bufA.Length, throwOnEndOfStream: false);
                int nb = sb.ReadAtLeast(bufB, bufB.Length, throwOnEndOfStream: false);
                if (na != nb) return false;
                if (na == 0) return true;
                if (!bufA.AsSpan(0, na).SequenceEqual(bufB.AsSpan(0, nb))) return false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
