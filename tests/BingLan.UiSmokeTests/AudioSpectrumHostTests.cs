using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

internal static class AudioSpectrumHostTests
{
    internal static void Run(Action<Window> show, Action pump)
    {
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
            window.ApplyDesktopExperience(experience);
            window.ApplyPlacement(component.Placement, false);
            Check(Require<Grid>(window, "BodySurface").Background is SolidColorBrush { Color.A: 1 },
                "解锁后的静音区域应保留最小非零透明度以接收拖动");
            Check(!window.IsWidgetLocked && spectrum.SpectrumBrush is SolidColorBrush updated
                && updated.Color == (Color)ColorConverter.ConvertFromString("#FFB35C"),
                "解锁和外观修改应即时应用");
            Check(!sampler.IsRunning && handler.RequestCount == 0, "重显和修改外观不能启动无关采样");
        }
        finally
        {
            window.CanClose = true;
            window.Close();
            pump();
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
