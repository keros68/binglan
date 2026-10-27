using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Interop;

/// <summary>
/// Drives the state watcher's event filter directly: every top-level window state
/// change must raise the notification at once — no IsZoomed pre-filter, or the
/// earliest events of a maximize (which can precede the zoomed style) would be
/// dropped — while child-object events stay filtered out. The notification must
/// arrive on the dispatcher thread. (The hook skips our own process, so the
/// callback is invoked by hand with a real window handle.)
/// </summary>
internal static class MaximizeStateWatcherTests
{
    internal static void Run(Action<Window> show, Action pump, Action<Func<bool>, string> pumpUntil)
    {
        var watcher = new MaximizeStateWatcher(Dispatcher.CurrentDispatcher);
        var raised = 0;
        watcher.TopLevelStateChanged += () => raised++;
        watcher.Start();
        var host = new Window
        {
            Width = 300,
            Height = 200,
            ShowInTaskbar = false,
            ShowActivated = false
        };
        show(host);
        var hwnd = new WindowInteropHelper(host).Handle;
        try
        {
            // 子对象（idObject 不是窗口）与子项（childId 非零）的状态变化应被过滤。
            watcher.OnWinEvent(0, DockNativeMethods.EventObjectStateChange, hwnd, -4, 0, 0, 0);
            watcher.OnWinEvent(0, DockNativeMethods.EventObjectStateChange, hwnd, 0, 1, 0, 0);
            pump();
            if (raised != 0)
            {
                throw new InvalidOperationException("子对象的状态变化不应触发通知");
            }

            // 顶层窗口的任意状态变化（包括尚未带上最大化样式的最早一拍）都应立即通知。
            watcher.OnWinEvent(0, DockNativeMethods.EventObjectStateChange, hwnd, 0, 0, 0, 0);
            pumpUntil(() => raised == 1, "顶层窗口的状态变化应触发一次通知");

            // 窗口内控件（子窗口）的状态变化不影响最大化判定，应被过滤。
            using var child = new HwndSource(new HwndSourceParameters("冰蓝测试子控件", 40, 20)
            {
                ParentWindow = hwnd,
                WindowStyle = unchecked((int)(NativeMethods.WsChild | 0x10000000))
            });
            watcher.OnWinEvent(0, DockNativeMethods.EventObjectStateChange, child.Handle, 0, 0, 0, 0);
            pump();
            if (raised != 1)
            {
                throw new InvalidOperationException("子窗口控件的状态变化不应触发通知");
            }

            // 一次排队前的连续事件只合并为一次通知。
            for (var i = 0; i < 50; i++)
            {
                watcher.OnWinEvent(0, DockNativeMethods.EventObjectStateChange, hwnd, 0, 0, 0, 0);
            }
            pumpUntil(() => raised == 2, "连续状态变化应合并为一次通知");
            pump();
            if (raised != 2)
            {
                throw new InvalidOperationException($"连续状态变化应只通知一次，实际 {raised - 1} 次");
            }
        }
        finally
        {
            watcher.Dispose();
            host.Close();
            pump();
        }
    }
}
