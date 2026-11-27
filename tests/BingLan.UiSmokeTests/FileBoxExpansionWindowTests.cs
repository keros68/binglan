using System.Windows;
using System.Windows.Controls;
using BingLan.App.Interop;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;

internal static class FileBoxExpansionWindowTests
{
    internal static void Run(Action<Window> show, Action pump, Action<Func<bool>, string> pumpUntil)
    {
        var priorMotion = CardMotionAnimator.Motion;
        var priorAnimations = CardMotionAnimator.AreSystemAnimationsEnabled;
        var work = SystemParameters.WorkArea;
        var state = new FileBoxState
        {
            ExpandUpward = true,
            Placement = new WindowPlacement
            {
                Left = work.Left + 100d,
                Top = work.Top + Math.Max(0d, (work.Height - 306d) / 2d),
                Width = 366d,
                Height = 306d
            }
        };
        var box = new FileBoxWindow(state);
        FileBoxWindow? restored = null;
        try
        {
            CardMotionAnimator.Motion = MotionLevel.Off;
            show(box);
            var header = (Border)box.FindName("TitleBarSurface");
            var body = (Grid)box.FindName("BodySurface");
            var titleBottom = TitleBottom(header);
            Check(Grid.GetRow(header) == 1, "向上展开的标题栏应在底部");

            box.SetCollapsed(true);
            pump();
            Check(Near(TitleBottom(header), titleBottom) && Near(box.ActualHeight, 48d),
                "向上折叠应保持标题栏屏幕位置");
            box.CaptureState();
            var collapsedTop = state.Placement.Top;
            var collapsedHeight = state.Placement.Height;
            box.SetCollapsed(false);
            pump();
            Check(Near(TitleBottom(header), titleBottom) && Near(box.ActualHeight, 306d),
                "再次展开应恢复高度并保持标题栏位置");

            CardMotionAnimator.Motion = MotionLevel.Standard;
            CardMotionAnimator.AreSystemAnimationsEnabled = () => true;
            box.SetCollapsed(true, animate: true);
            pump();
            Check(box.IsFoldAnimating && body.VerticalAlignment == VerticalAlignment.Bottom,
                "向上折叠动画应贴住窗口底边");
            box.CaptureState();
            Check(Near(state.Placement.Top, collapsedTop) && Near(state.Placement.Height, collapsedHeight),
                "折叠动画中保存应记录最终标题位置");
            box.SetCollapsed(false, animate: true);
            pumpUntil(() => !box.IsFoldAnimating, "中断折叠后展开动画未结束");
            pump();
            Check(Near(TitleBottom(header), titleBottom) && !state.IsCollapsed,
                "反向打断折叠动画应保留展开状态和标题位置");

            box.SetCollapsed(true, animate: true);
            pumpUntil(() => !box.IsFoldAnimating, "向上折叠动画未结束");
            pump();
            Check(Near(TitleBottom(header), titleBottom) && Near(box.ActualHeight, 48d),
                "向上折叠动画完成后标题应留在原位置");
            box.CaptureState();
            restored = new FileBoxWindow(state);
            show(restored);
            Check(Near(restored.Top, box.Top) && Near(restored.ActualHeight, 48d),
                "折叠状态重建窗口应恢复同一标题位置");
            restored.CanClose = true;
            restored.Close();
            restored = null;

            CardMotionAnimator.Motion = MotionLevel.Off;
            box.SetCollapsed(false);
            pump();
            var bounds = Bounds(box);
            state.EdgeDockSide = FileBoxEdgeDockSide.Left;
            box.EnterEdgeDockedExpandedVisual(FileBoxEdgeDockSide.Left, bounds, animate: false);
            pump();
            Check(Grid.GetRow(header) == 0 && state.ExpandUpward,
                "贴边展开应使用顶部标题并保留本机向上展开偏好");
            state.EdgeDockSide = null;
            box.ExitEdgeDockedVisual(bounds);
            pump();
            Check(Grid.GetRow(header) == 1, "退出贴边隐藏后应恢复底部标题");

            box.SetExpandUpward(false);
            var top = box.Top;
            box.SetCollapsed(true);
            pump();
            Check(Near(box.Top, top) && Grid.GetRow(header) == 0,
                "关闭向上展开后应保持原有顶部标题折叠行为");
        }
        finally
        {
            CardMotionAnimator.Motion = priorMotion;
            CardMotionAnimator.AreSystemAnimationsEnabled = priorAnimations;
            if (restored is not null)
            {
                restored.CanClose = true;
                restored.Close();
            }
            box.CanClose = true;
            box.Close();
            pump();
        }
    }

    private static double TitleBottom(FrameworkElement title) => title.PointToScreen(new Point(0d, title.ActualHeight)).Y;

    private static PixelRect Bounds(FileBoxWindow box)
    {
        Check(DockNativeMethods.GetWindowRect(box.WindowHandle, out var native), "应能读取文件盒窗口矩形");
        return new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 2d;

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
