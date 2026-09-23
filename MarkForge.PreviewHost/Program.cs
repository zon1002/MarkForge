using System.Globalization;

namespace MarkForge.PreviewHost;

static class Program
{
    /// <summary>
    /// Launched by MarkForge.PreviewHandler.dll, never by the user:
    /// <c>MarkForge.PreviewHost.exe "&lt;file&gt;" &lt;parentHwnd&gt; &lt;left&gt; &lt;top&gt; &lt;right&gt; &lt;bottom&gt;</c>
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 6
            || !ulong.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong parent)
            || !TryParseInts(args.AsSpan(2, 4), out int[] rect))
        {
            return 1;
        }

        // A preview that fails must fail quietly — never pop a dialog over Explorer — but
        // leave a trace in error.log so a blank preview pane can be diagnosed.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { LogError(e.Exception); Application.Exit(); };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { LogError(e.ExceptionObject as Exception); Environment.Exit(2); };

        ApplicationConfiguration.Initialize();
        using var surface = new PreviewSurface(
            args[0],
            new IntPtr(unchecked((long)parent)),
            Rectangle.FromLTRB(rect[0], rect[1], rect[2], rect[3]));
        surface.CreateControl();
        Application.Run(new ApplicationContext());
        return 0;
    }

    /// <summary>Appends to %LOCALAPPDATA%\MarkForge\PreviewHost\error.log (kept under 256 KB).</summary>
    internal static void LogError(Exception? ex, string? context = null)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MarkForge", "PreviewHost");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "error.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256 * 1024) File.Delete(path);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context} {ex}{Environment.NewLine}");
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool TryParseInts(ReadOnlySpan<string> values, out int[] result)
    {
        result = new int[values.Length];
        for (int i = 0; i < values.Length; i++)
        {
            if (!int.TryParse(values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i])) return false;
        }
        return true;
    }
}
