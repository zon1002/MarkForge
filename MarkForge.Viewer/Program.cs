namespace MarkForge.Viewer;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        Application.ThreadException += (_, e) => ShowFatalError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ShowFatalError(e.ExceptionObject as Exception);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        ApplicationConfiguration.Initialize();
        Application.Run(new ViewerForm(args.Length > 0 ? args[0] : null));
    }

    private static void ShowFatalError(Exception? ex)
    {
        MessageBox.Show(
            "發生未預期的錯誤，此操作可能未完成，但程式將盡量繼續執行。\n\n" + (ex?.Message ?? "（無詳細資訊）"),
            "MarkForge Viewer - 發生錯誤",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
