using System.IO;
using System.Windows.Threading;
using BingLan.App.Services;

/// <summary>
/// Blocks the UI thread past the stall threshold and checks that stall.log names the
/// running operation and then records the recovery.
/// </summary>
internal static class UiDiagnosticsTests
{
    internal static void Run(Action pump, Action<Func<bool>, string> pumpUntil)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BingLan-stall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // A monitor started by an earlier test keeps running and writes here from now on.
            UiDiagnostics.Start(Dispatcher.CurrentDispatcher, directory);
            var path = Path.Combine(directory, "stall.log");
            pump();
            var offset = File.Exists(path) ? new FileInfo(path).Length : 0;
            string Tail()
            {
                if (!File.Exists(path)) return string.Empty;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
                return new StreamReader(stream).ReadToEnd();
            }

            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(BlockForStallTest));
            pump();
            pumpUntil(() => Tail().Contains("界面恢复响应"), "界面恢复后应记录卡住时长");
            var tail = Tail();
            if (!tail.Contains("界面卡住") || !tail.Contains(nameof(BlockForStallTest)))
            {
                throw new InvalidOperationException($"卡住记录应写明正在执行的任务：{tail}");
            }
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static void BlockForStallTest() => Thread.Sleep(2600);
}
