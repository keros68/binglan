using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace BingLan.App.Services;

/// <summary>
/// Starts programs, files and folders off the UI thread. ShellExecute can wait seconds
/// on the target's handler (a replacement file manager, a DDE verb, antivirus checks),
/// and a UI thread waiting there freezes every card and the dock with it.
/// </summary>
internal static class ShellLauncher
{
    /// <summary>Starts the target; the task carries the failure, or null on success.</summary>
    internal static Task<Exception?> StartAsync(ProcessStartInfo start) =>
        Task.Run<Exception?>(() =>
        {
            try
            {
                Process.Start(start)?.Dispose();
                return null;
            }
            catch (Exception exception) when (exception is Win32Exception
                or InvalidOperationException or FileNotFoundException)
            {
                return exception;
            }
        });
}
