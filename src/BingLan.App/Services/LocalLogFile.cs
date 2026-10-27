using System.Diagnostics;
using System.IO;

namespace BingLan.App.Services;

/// <summary>Appends a line to a small local log, starting over once it grows past its limit.</summary>
internal static class LocalLogFile
{
    private static readonly object Gate = new();

    internal static void Append(string path, string line, long maximumBytes)
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > maximumBytes)
                {
                    File.Delete(path);
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Debug.WriteLine($"本地日志写入失败：{exception.Message}");
            }
        }
    }
}
