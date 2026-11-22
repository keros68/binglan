using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace BingLan.App.Services;

/// <summary>
/// Passive, zero-dependency integration with QuickLook: when QuickLook is already running
/// on the machine, pressing Space on a real file or folder toggles its preview via the
/// documented named pipe. Never bundles, installs or starts QuickLook on its own.
/// </summary>
internal static class QuickPreviewLauncher
{
    private const int ConnectTimeoutMs = 150;

    internal static Task<bool> TryTogglePreviewAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Task.FromResult(false);
        }

        return Task.Run(() => TryToggleQuickLook(path));
    }

    private static bool TryToggleQuickLook(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }
            if (!IsQuickLookRunning())
            {
                return false;
            }
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrEmpty(sid))
            {
                return false;
            }

            var pipeName = $"QuickLook.App.Pipe.{sid}";
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(ConnectTimeoutMs);
            var payload = Encoding.UTF8.GetBytes($"QuickLook.App.PipeMessage.Toggle|{path}\n");
            client.Write(payload, 0, payload.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsQuickLookRunning()
    {
        var processes = Process.GetProcessesByName("QuickLook");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
