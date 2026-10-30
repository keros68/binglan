using System.Diagnostics;
using System.IO;

namespace BingLan.App.Services;

/// <summary>
/// Starts the uninstaller that setup placed next to the app. The portable zip copy has
/// none, so it offers no uninstall.
/// </summary>
internal static class AppUninstaller
{
    // Inno Setup names it unins000.exe while only one app is installed in the folder.
    private const string FileName = "unins000.exe";

    internal static string? Find(string? executablePath)
    {
        if (string.IsNullOrEmpty(executablePath) ||
            Path.GetDirectoryName(executablePath) is not { } folder)
        {
            return null;
        }
        var path = Path.Combine(folder, FileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Returns null once started, or why it could not start.</summary>
    internal static string? Start(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return null;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception
            or InvalidOperationException)
        {
            return exception.Message;
        }
    }
}
