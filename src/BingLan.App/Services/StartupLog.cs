using System.IO;
using System.Reflection;

namespace BingLan.App.Services;

/// <summary>
/// A short local record of when each startup step that touches Explorer began and ended,
/// so a desktop that hangs after logon can be traced to a step, plus actions that failed
/// unexpectedly. It stays on this machine and is trimmed when it grows past 64 KB.
/// </summary>
internal static class StartupLog
{
    private const long MaximumBytes = 64 * 1024;
    private const long MaximumCrashBytes = 128 * 1024;
    internal static readonly string BuildVersion =
        typeof(StartupLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(StartupLog).Assembly.GetName().Version?.ToString()
        ?? "unknown";
    private static string? _path;
    private static string? _crashPath;

    internal static string DataDirectory =>
        Path.GetDirectoryName(_path)
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BingLanWidgets");

    internal static void Open(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "startup.log");
        _crashPath = Path.Combine(dataDirectory, "crash.log");
    }

    internal static void Write(string step)
    {
        if (_path is not { } path)
        {
            return;
        }

        LocalLogFile.Append(
            path,
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  [{BuildVersion}]  开机后 {Environment.TickCount64 / 1000} 秒  {step}",
            MaximumBytes);
    }

    /// <summary>
    /// Writes an unhandled or fatal exception to the local <c>crash.log</c> (with OS build,
    /// app version, thread metadata and stack trace; never uploaded). Works even if startup
    /// fails before <see cref="Open"/> has run.
    /// </summary>
    internal static void WriteCrash(string source, object? exceptionObject)
    {
        var crashPath = _crashPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BingLanWidgets",
            "crash.log");
        var thread = Thread.CurrentThread;
        var summary = exceptionObject is Exception ex
            ? $"{ex.GetType().FullName}: {ex.Message}"
            : exceptionObject?.ToString() ?? "未知异常";
        var detail = exceptionObject?.ToString() ?? "未知异常";
        var entry =
            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] version={BuildVersion} os={Environment.OSVersion.VersionString} " +
            $"x64={Environment.Is64BitProcess} source={source} thread={thread.ManagedThreadId}({thread.Name ?? "unnamed"})\n" +
            $"{detail}\n";
        LocalLogFile.Append(crashPath, entry, MaximumCrashBytes);
        Write($"异常({source})：{summary}");
    }
}
