using System.IO;

namespace BingLan.App.Services;

/// <summary>
/// A short local record of when each startup step that touches Explorer began and ended,
/// so a desktop that hangs after logon can be traced to a step, plus actions that failed
/// unexpectedly. It stays on this machine and is trimmed when it grows past 64 KB.
/// </summary>
internal static class StartupLog
{
    private const long MaximumBytes = 64 * 1024;
    private static string? _path;

    internal static void Open(string dataDirectory) => _path = Path.Combine(dataDirectory, "startup.log");

    internal static void Write(string step)
    {
        if (_path is not { } path)
        {
            return;
        }

        LocalLogFile.Append(
            path,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  开机后 {Environment.TickCount64 / 1000} 秒  {step}",
            MaximumBytes);
    }
}
