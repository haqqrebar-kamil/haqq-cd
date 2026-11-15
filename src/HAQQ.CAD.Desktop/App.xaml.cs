using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace HAQQ.CAD.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => WriteLog(args.ExceptionObject as Exception);
        base.OnStartup(e);

        // The same program file doubles as its installer: a copy named HAQQ-CAD-Setup-*.exe installs
        // itself for the current Windows user, starts the installed copy and exits.
        if (SelfInstaller.IsSetupFile(Environment.ProcessPath))
        {
            SelfInstaller.Run(Environment.ProcessPath!);
            Shutdown();
            return;
        }

        new MainWindow().Show();
    }

    /// <summary>
    /// Last line of defence. If the main window is already up, record the error and keep it alive
    /// so the user can still save (unsaved work is also covered by autosave). If the failure
    /// happened while starting, say so and exit instead of leaving an invisible process behind.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        string logPath = WriteLog(e.Exception);
        bool started = MainWindow is { IsLoaded: true };
        string where = logPath.Length > 0 ? "\n\nDetails were written to:\n" + logPath : "";

        if (started)
        {
            // One message at a time: a problem during screen layout can repeat many times a second.
            e.Handled = true;
            if (_showingError) return;
            _showingError = true;
            try
            {
                ShowProblem(e.Exception, where);
            }
            finally
            {
                _showingError = false;
            }
            return;
        }

        MessageBox.Show(
            "HAQQ CAD could not start:\n\n" + Describe(e.Exception) + where,
            "HAQQ CAD", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private bool _showingError;

    private static void ShowProblem(Exception exception, string where)
    {
        {
            MessageBox.Show(
                "HAQQ CAD ran into an unexpected problem:\n\n" + exception.Message +
                "\n\nYou can keep working, but please save your drawing under a new name now." + where,
                "HAQQ CAD", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Describe(Exception exception)
    {
        // The useful message of a XAML failure is usually on the innermost exception.
        var inner = exception;
        while (inner.InnerException is not null) inner = inner.InnerException;
        return ReferenceEquals(inner, exception) ? exception.Message : exception.Message + "\n\n" + inner.Message;
    }

    /// <summary>Appends the error to error.log beside the program and in the user's HAQQ CAD data folder.</summary>
    private static string WriteLog(Exception? exception)
    {
        if (exception is null) return "";
        string text = $"{DateTime.Now:u}\r\n{exception}\r\n\r\n";
        string written = "";
        string[] folders =
        {
            AppContext.BaseDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HAQQ CAD"),
        };
        foreach (string folder in folders)
        {
            try
            {
                Directory.CreateDirectory(folder);
                string path = Path.Combine(folder, "error.log");
                File.AppendAllText(path, text);
                if (written.Length == 0) written = path;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return written;
    }
}
