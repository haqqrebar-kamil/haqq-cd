using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HAQQ.CAD.Desktop;

/// <summary>
/// Per-user installation without a separate setup program. The single-file HaqqCad.exe, when it is
/// named <c>HAQQ-CAD-Setup-*.exe</c>, copies itself to <c>%LOCALAPPDATA%\Programs\HAQQ CAD\HaqqCad.exe</c>,
/// adds Desktop and Start-menu shortcuts and starts the installed copy. No administrator rights are needed.
/// </summary>
internal static class SelfInstaller
{
    private const string SetupPrefix = "HAQQ-CAD-Setup";

    public static bool IsSetupFile(string? programPath) =>
        programPath is not null &&
        Path.GetFileName(programPath).StartsWith(SetupPrefix, StringComparison.OrdinalIgnoreCase);

    public static void Run(string setupPath)
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "HAQQ CAD");
        string target = Path.Combine(folder, "HaqqCad.exe");

        var answer = MessageBox.Show(
            "Install HAQQ CAD for this Windows user?\n\nThe program is copied to:\n" + folder +
            "\n\nand a \"HAQQ CAD\" shortcut is added to the Desktop and the Start menu.",
            "HAQQ CAD Setup", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            Directory.CreateDirectory(folder);
            if (File.Exists(target)) RemoveOrSetAside(target);
            File.Copy(setupPath, target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show("HAQQ CAD could not be installed:\n\n" + ex.Message +
                            "\n\nIf HAQQ CAD is open, close it and run this setup again.",
                "HAQQ CAD Setup", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        // The copy carries the "downloaded from the internet" mark of the setup file; remove it so that
        // the installed program starts without a second Windows warning. Not being able to is harmless.
        try { File.Delete(target + ":Zone.Identifier"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { }

        bool shortcuts = TryCreateShortcut(Environment.SpecialFolder.DesktopDirectory, target, folder)
                       & TryCreateShortcut(Environment.SpecialFolder.Programs, target, folder);
        if (!shortcuts)
            MessageBox.Show("HAQQ CAD was installed, but a shortcut could not be created. You can start it from:\n" + target,
                "HAQQ CAD Setup", MessageBoxButton.OK, MessageBoxImage.Information);

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = folder });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show("HAQQ CAD was installed but could not be started:\n\n" + ex.Message,
                "HAQQ CAD Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Deletes the installed program, or renames it out of the way when it is running (Windows allows that).</summary>
    private static void RemoveOrSetAside(string target)
    {
        string old = target + ".old";
        try
        {
            File.Delete(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(target, old);
        }
    }

    private static bool TryCreateShortcut(Environment.SpecialFolder where, string target, string workingFolder)
    {
        try
        {
            string folder = Environment.GetFolderPath(where);
            if (folder.Length == 0) return false;
            Directory.CreateDirectory(folder);
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return false;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(Path.Combine(folder, "HAQQ CAD.lnk"));
            link.TargetPath = target;
            link.WorkingDirectory = workingFolder;
            link.Description = "HAQQ CAD";
            link.Save();
            return true;
        }
        catch (Exception)
        {
            return false;       // shortcuts are a convenience; the installation itself has succeeded
        }
    }
}
