using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Dock;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using Window = System.Windows.Window;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;

internal static class DockGlassPreview
{
    // Interactive verification uses the production windows with disposable state.
    internal static void Run(System.Windows.Application app)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var scene = new Canvas { Background = Brushes.DarkSlateBlue, ClipToBounds = true };
        for (var i = 0; i < 110; i++)
        {
            var stripe = new Border { Width = 8, Height = 480, Background = i % 2 == 0 ? Brushes.Coral : Brushes.DarkSlateBlue };
            Canvas.SetLeft(stripe, i * 8);
            scene.Children.Add(stripe);
        }
        var root = new DockPanel();
        DockPanel.SetDock(panel, System.Windows.Controls.Dock.Top);
        root.Children.Add(panel);
        root.Children.Add(scene);
        var host = new Window { Title = "冰蓝正式 Dock 玻璃验收", Width = 900, Height = 500, Content = root, Topmost = true };
        var state = new DockState { IsEnabled = true, VisibilityMode = DockVisibilityMode.SmartHide,
            PinnedApps = [new() { DisplayName = "资源管理器", ExecutablePath = @"C:\Windows\explorer.exe" },
                new() { DisplayName = "记事本", ExecutablePath = @"C:\Windows\System32\notepad.exe" }] };
        var dock = new IceBlueDockWindow(state);
        DockAppBarController? bar = null;
        DockHandleWindow? handle = null;
        var output = Path.GetFullPath("artifacts/dock-glass/release-0.2.25");
        Directory.CreateDirectory(output);
        void Invoke(string name, params object[] args) => typeof(IceBlueDockWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(dock, args);
        T Field<T>(string name) => (T)typeof(IceBlueDockWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(dock)!;
        void Action(string text, System.Action action)
        {
            var button = new Button { Content = text, Margin = new Thickness(6), Padding = new Thickness(12, 8, 12, 8), Foreground = Brushes.Black, Background = Brushes.LightGray };
            button.Click += (_, _) => action();
            panel.Children.Add(button);
        }
        Action("收起到把手", () =>
        {
            typeof(IceBlueDockWindow).GetField("_hiddenBySmartHide", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dock, true);
            Invoke("BeginSmartHide");
        });
        Action("从把手展开", () => Invoke("ShowDock"));
        void Capture(string name)
        {
            var at = scene.PointToScreen(new System.Windows.Point());
            var dpi = PresentationSource.FromVisual(host)!.CompositionTarget.TransformToDevice;
            using var bitmap = new System.Drawing.Bitmap((int)(scene.ActualWidth * dpi.M11), (int)(scene.ActualHeight * dpi.M22));
            using var graphics = System.Drawing.Graphics.FromImage(bitmap);
            graphics.CopyFromScreen((int)at.X, (int)at.Y, 0, 0, bitmap.Size);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
        Action("保存受控画面", () => Capture($"dock-{DateTime.Now:HHmmss}"));
        Action("退出验收", host.Close);
        host.Loaded += async (_, _) =>
        {
            dock.Owner = host;
            dock.Show();
            await Task.Delay(1500);
            if (!host.IsVisible) return;
            Field<MaximizeStateWatcher>("_maximizeWatcher").Dispose();
            Field<WindowEventWatcher>("_watcher").Dispose();
            Field<DispatcherTimer>("_foregroundTimer").Stop();
            bar = Field<DockAppBarController>("_appBar");
            handle = Field<DockHandleWindow>("_handleWindow");
            var at = scene.PointToScreen(new System.Windows.Point(25, 120));
            var dpi = PresentationSource.FromVisual(host)!.CompositionTarget.TransformToDevice.M11;
            bar.SetFloatingOverride(new PixelRect((int)at.X, (int)at.Y, (int)(at.X + 700 * dpi), (int)(at.Y + 80 * dpi)));
            var corner = scene.PointToScreen(new System.Windows.Point(45, 215));
            state.HiddenHandleLeftDip = (corner.X - bar.Monitor!.Bounds.Left) / dpi;
            state.HiddenHandleTopDip = (corner.Y - bar.Monitor.Bounds.Top) / dpi;
            handle.MoveTo(new PixelRect((int)corner.X, (int)corner.Y, (int)(corner.X + 44 * dpi), (int)(corner.Y + 44 * dpi)));
            Field<DispatcherTimer>("_foregroundTimer").Stop();
            Invoke("ShowDock");
            if (Environment.GetCommandLineArgs().Contains("--dock-glass-capture"))
            {
                await Task.Delay(400);
                Capture("01-expanded");
                typeof(IceBlueDockWindow).GetField("_hiddenBySmartHide", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(dock, true);
                Invoke("BeginSmartHide");
                await Task.Delay(500);
                Capture("02-handle");
                Invoke("ShowDock");
                await Task.Delay(400);
                Capture("03-handle-expanded");
                host.Close();
            }
        };
        host.Closed += (_, _) => { dock.Close(); app.Shutdown(); };
        app.Run(host);
    }
}
