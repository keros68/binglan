using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

internal static class AudioSpectrumHostTests
{
    internal static void Run(Action<Window> show, Action pump)
    {
        CheckRestingSpectrum();
        var experience = DesktopExperienceRules.CreateDefault();
        var component = experience.GetComponent(DesktopComponentKind.AudioVisualizer);
        Check(!component.IsVisible, "频谱应默认关闭");
        component.IsVisible = true;
        component.Appearance.BackgroundOpacity = 1d;
        component.Appearance.BackgroundColor = "#FFFFFF";
        component.Appearance.TextColor = "#58D1FF";
        using var sampler = new WindowsPerformanceSamplingService();
        using var handler = new RejectWeatherRequests();
        var state = new InformationWidgetState
        {
            WeatherCity = "测试城市",
            WeatherLatitude = 45d,
            WeatherLongitude = 120d
        };
        var window = new InformationWidgetWindow(state, sampler, new WeatherService(handler),
            experience, DesktopComponentKind.AudioVisualizer);
        window.SetAudioSpectrumSuspended(true);
        window.ApplyPlacement(component.Placement, true);
        try
        {
            show(window);
            pump();
            var host = Require<ContentControl>(window, "SpectrumHost");
            var spectrum = host.Content as AudioSpectrumView
                ?? throw new InvalidOperationException("频谱内容未创建");
            Check(host.IsVisible && spectrum.IsVisible, "启用后频谱应可见");
            Check(spectrum.BarWidth == 0.4d, "频谱默认柱宽应为频段间距的 40%");
            Check(window.IsWidgetLocked, "频谱应复用布局锁定");
            Check(Require<StackPanel>(window, "ClockPanel").Visibility == Visibility.Collapsed
                && Require<Border>(window, "WeatherCard").Visibility == Visibility.Collapsed
                && Require<Border>(window, "PerformanceCard").Visibility == Visibility.Collapsed,
                "频谱窗口不能显示时钟、天气或性能卡片");
            Check(Require<Grid>(window, "BodySurface").Background is SolidColorBrush { Color.A: 0 }
                && Require<Grid>(window, "BodySurface").Effect is null
                && Require<Border>(window, "Surface").BorderThickness == new Thickness(0),
                "保留旧背景设置也应只显示频谱，无背景、边框或阴影");
            Check(spectrum.SpectrumBrush is SolidColorBrush brush
                && brush.Color == (Color)ColorConverter.ConvertFromString("#58D1FF"),
                "频谱应使用组件文字颜色");
            Check(!sampler.IsRunning && handler.RequestCount == 0, "频谱不能启动性能或天气采样");
            var ambient = (DispatcherTimer?)typeof(InformationWidgetWindow)
                .GetField("_ambientTimer", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
            Check(ambient is { IsEnabled: false }, "频谱不应注册分钟刷新计时器");

            window.HideFromApp();
            pump();
            Check(!spectrum.IsVisible, "隐藏桌面组件应隐藏频谱");
            window.Show();
            pump();
            Check(spectrum.IsVisible, "重新显示应恢复频谱");
            component.Appearance.TextColor = "#FFB35C";
            component.SpectrumBarWidth = 0.68d;
            window.ApplyDesktopExperience(experience);
            window.ApplyPlacement(component.Placement, false);
            Check(Require<Grid>(window, "BodySurface").Background is SolidColorBrush { Color.A: 1 },
                "解锁后的静音区域应保留最小非零透明度以接收拖动");
            Check(!window.IsWidgetLocked && spectrum.SpectrumBrush is SolidColorBrush updated
                && updated.Color == (Color)ColorConverter.ConvertFromString("#FFB35C"),
                "解锁和外观修改应即时应用");
            Check(ReferenceEquals(host.Content, spectrum) && spectrum.BarWidth == 0.68d,
                "修改柱宽应实时应用到已有频谱控件");
            Check(!sampler.IsRunning && handler.RequestCount == 0, "重显和修改外观不能启动无关采样");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            pump();
        }
    }

    private static void CheckRestingSpectrum()
    {
        const int width = 480, height = 100;
        var view = new AudioSpectrumView { SpectrumBrush = Brushes.White };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));

        byte[] Render()
        {
            view.UpdateLayout();
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(view);
            var pixels = new byte[width * height * 4];
            image.CopyPixels(pixels, width * 4, 0);
            return pixels;
        }

        var resting = Render();
        // At 480 DIP each of the 48 bands occupies a ten-pixel cell.
        for (var x = 0; x < width; x += 10)
        {
            Check(resting[((height - 1) * width + x + 5) * 4 + 3] > 0
                && resting[((height - 1) * width + x) * 4 + 3] == 0,
                "安静时每个频段都应显示独立短线");
        }
        Check(resting.AsSpan(0, (height - 2) * width * 4).IndexOfAnyExcept((byte)0) < 0,
            "静止短线上方应保持透明");
        view.SetSuspended(true);
        Check(Render().All(value => value == 0), "全屏暂停时频谱应完全透明");
        view.SetSuspended(false);
        Check(resting.SequenceEqual(Render()), "退出暂停后应立即恢复静止短线");

        var levels = (float[])typeof(AudioSpectrumView)
            .GetField("_levels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(view)!;
        Array.Fill(levels, 1f);
        var renderedWidths = new List<int>();
        foreach (var barWidth in new[] { 0.25d, 0.4d, 0.8d })
        {
            view.BarWidth = barWidth;
            var pixels = Render();
            renderedWidths.Add(Enumerable.Range(0, width)
                .Sum(x => (int)pixels[((height / 2) * width + x) * 4 + 3]));
        }
        Check(renderedWidths[0] < renderedWidths[1] && renderedWidths[1] < renderedWidths[2],
            "柱宽应实际改变渲染的柱体宽度");
        view.BarWidth = double.NaN;
        Check(view.BarWidth == 0.4d, "无效柱宽应恢复默认");
        view.BarWidth = 5d;
        Check(view.BarWidth == 0.8d, "柱宽不能超过频段间距的 80%");
        view.BarWidth = 0.4d;
        foreach (var (band, column) in new[] { (0, 23), (1, 24), (22, 12), (23, 35), (46, 0), (47, 47) })
        {
            Array.Clear(levels);
            levels[band] = 1;
            view.InvalidateVisual();
            var pixels = Render();
            for (var i = 0; i < levels.Length; i++)
            {
                var alpha = pixels[((height / 2) * width + i * 10 + 5) * 4 + 3];
                Check((alpha > 0) == (i == column), $"频段 {band} 应独立显示在第 {column + 1} 柱");
            }
        }
    }

    private static T Require<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"缺少控件 {name}");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RejectWeatherRequests : HttpMessageHandler
    {
        internal int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("频谱不得请求天气");
        }
    }
}
