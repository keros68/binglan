using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using BingLan.App.Interop;
using BingLan.App.Windows;
using BingLan.Core.Dock;

/// <summary>
/// Drives the hidden-dock handle without a mouse: window style, drag behaviour and
/// visibility switching.
/// </summary>
internal static class DockHandleTests
{
    private const int GwlExStyle = -20;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    internal static void Run(Action<Window> show, Action pump)
    {
        var handle = new DockHandleWindow();
        var dragStarted = 0;
        var dragEnded = 0;
        var clicked = 0;
        handle.DragStarted += () => dragStarted++;
        handle.DragEnded += () => dragEnded++;
        handle.Clicked += () => clicked++;
        show(handle);
        try
        {
            var hwnd = new WindowInteropHelper(handle).Handle;
            var style = (long)GetWindowLongPtr(hwnd, GwlExStyle);
            if ((style & NativeMethods.WsExToolWindow) == 0)
            {
                throw new InvalidOperationException("把手窗口缺少 ToolWindow 扩展样式，会出现在任务栏");
            }
            if ((style & DockNativeMethods.WsExNoActivate) == 0)
            {
                throw new InvalidOperationException("把手窗口缺少 NoActivate 扩展样式，点击会抢焦点");
            }

            handle.MoveTo(new PixelRect(600, 500, 644, 544));
            pump();
            DockNativeMethods.GetWindowRect(hwnd, out var placed);
            if (placed.Left != 600 || placed.Top != 500)
            {
                throw new InvalidOperationException($"把手没有移动到指定位置：{placed.Left},{placed.Top}");
            }

            // 3px 抖动阈值内的移动不算拖动。
            handle.BeginDrag(700, 522);
            handle.DragTo(701, 522);
            if (dragStarted != 0)
            {
                throw new InvalidOperationException("阈值内的微小移动不应开始拖动");
            }

            handle.DragTo(712, 530);
            pump();
            if (dragStarted != 1)
            {
                throw new InvalidOperationException("超过阈值后应开始拖动");
            }
            DockNativeMethods.GetWindowRect(hwnd, out var dragged);
            if (dragged.Left != 612 || dragged.Top != 508)
            {
                throw new InvalidOperationException(
                    $"拖动应使把手跟随指针位移：期望 612,508，实际 {dragged.Left},{dragged.Top}");
            }

            handle.EndDrag();
            if (dragEnded != 1)
            {
                throw new InvalidOperationException("松手后应触发一次拖动结束");
            }

            // 按下即松开、无位移的单击不触发位置提交，仅在正常释放时触发点击唤出。
            handle.BeginDrag(700, 522);
            handle.EndDrag(allowClick: false);
            if (dragEnded != 1 || clicked != 0)
            {
                throw new InvalidOperationException("捕获丢失导致的无位移结束不应触发拖动结束或点击");
            }

            handle.BeginDrag(700, 522);
            handle.EndDrag(allowClick: true);
            if (dragEnded != 1 || clicked != 1)
            {
                throw new InvalidOperationException("无位移的正常单击应触发一次 Clicked 而不触发拖动结束");
            }

            handle.SetVisible(false);
            pump();
            if (handle.IsVisible)
            {
                throw new InvalidOperationException("SetVisible(false) 后把手应隐藏");
            }
            handle.SetVisible(true);
            pump();
            if (!handle.IsVisible)
            {
                throw new InvalidOperationException("SetVisible(true) 后把手应显示");
            }
        }
        finally
        {
            handle.Close();
        }
    }
}
