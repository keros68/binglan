using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using BingLan.App.Interop;

namespace BingLan.App.TopBar;

internal static class BatteryHubCli
{
    private const string ExecutableName = "BatteryHub.exe";
    private const int MaxOutputCharacters = 256 * 1024;
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(15);

    internal static string? GetConfiguredPath(string? path)
    {
        // Only accept a drive-qualified path chosen on this machine, never a share or device path.
        if (path is not { Length: > 3 } || !char.IsAsciiLetter(path[0]) || path[1] != ':'
            || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            return string.Equals(Path.GetFileName(fullPath), ExecutableName, StringComparison.OrdinalIgnoreCase)
                ? fullPath
                : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    internal static string? ResolveLocalExecutable(string? path)
    {
        if (GetConfiguredPath(path) is not { } configuredPath) return null;

        try
        {
            if (!IsLocalDrive(configuredPath)) return null;
            using var file = File.OpenHandle(configuredPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

            // Check the file after resolving links as well as the configured drive.
            const string windowsPathPrefix = @"\\?\";
            var buffer = new StringBuilder(260);
            var length = NativeMethods.GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
            if (length >= buffer.Capacity && length <= 32768)
            {
                buffer.EnsureCapacity((int)length);
                length = NativeMethods.GetFinalPathNameByHandleW(file, buffer, (uint)buffer.Capacity, 0);
            }
            if (length <= windowsPathPrefix.Length || length >= buffer.Capacity
                || !buffer.ToString().StartsWith(windowsPathPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            var finalPath = GetConfiguredPath(buffer.ToString(windowsPathPrefix.Length, (int)length - windowsPathPrefix.Length));
            return finalPath is not null && IsLocalDrive(finalPath) ? finalPath : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static bool IsLocalDrive(string path) =>
        new DriveInfo(Path.GetPathRoot(path)!).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram;

    internal static Task<string?> ReadAsync(string executablePath, CancellationToken cancellationToken) =>
        Task.Run(() => ScanAsync(executablePath, cancellationToken), cancellationToken);

    private static async Task<string?> ScanAsync(string executablePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ResolveLocalExecutable(executablePath) is not { } localPath)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ScanTimeout);
        var startInfo = new ProcessStartInfo
        {
            FileName = localPath,
            WorkingDirectory = Path.GetDirectoryName(localPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, throwOnInvalidBytes: true)
        };
        startInfo.ArgumentList.Add("--json");
        using var process = new Process { StartInfo = startInfo };
        Task<string>? readOutput = null;
        Task? drainErrors = null;
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (!process.Start())
            {
                return null;
            }

            drainErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            readOutput = ReadBoundedOutputAsync(process.StandardOutput, timeout.Token);
            var output = await readOutput.WaitAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await drainErrors.WaitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidDataException
            or InvalidOperationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            timeout.Cancel();
            await StopProcessAsync(process).ConfigureAwait(false);
            ObservePipeFault(readOutput);
            ObservePipeFault(drainErrors);
        }
    }

    private static async Task<string> ReadBoundedOutputAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) is var count && count > 0)
        {
            if (count > MaxOutputCharacters - output.Length)
            {
                throw new InvalidDataException("BatteryHub 输出超过大小限制。");
            }
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }

    private static async Task StopProcessAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await process.WaitForExitAsync(stopDeadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception
            or AggregateException or OperationCanceledException)
        {
            // Process startup may have failed, or the process may already have exited.
        }
    }

    private static void ObservePipeFault(Task? task)
    {
        if (task is not null && !task.IsCompletedSuccessfully)
        {
            _ = task.ContinueWith(static faulted => { _ = faulted.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
