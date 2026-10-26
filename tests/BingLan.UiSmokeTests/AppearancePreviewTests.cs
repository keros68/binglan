using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using BingLan.App.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using BingLan.App.Themes;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Button = System.Windows.Controls.Button;

internal static class AppearancePreviewTests
{
    internal static void Run(Action<Window> show, Action pump)
    {
        var state = LocalStateStore.CreateDefault();
        state.Style.CardShadow = CardShadow.Strong;
        state.Style.CardSpacing = 24;
        state.Style.Motion = MotionLevel.Off;
        state.Style.AccentColor = "#A1B2C3";
        state.Dock.FollowCardLook = false;
        state.Dock.SurfaceColor = "#334455";
        var information = new InformationWidgetState { GreetingName = "PRIVATE_APPEARANCE_MARKER" };
        state.InformationWidgets.Add(information);
        state.TodoWidgets.Add(new TodoWidgetState { Title = "PRIVATE_APPEARANCE_MARKER" });
        state.FileBoxes.Add(new FileBoxState { Title = "PRIVATE_APPEARANCE_MARKER" });
        var calls = 0;
        var window = new SettingsWindow(new CityLookupService(), information, state.DesktopExperience,
            (_, _) => { }, _ => { }, _ => { }, dockState: state.Dock,
            maintenance: new SettingsMaintenance { Style = state.Style, ApplyGlass = () => calls++ });
        show(window);
        var original = JsonSerializer.Serialize(state);
        try
        {
            OpenDialog(window, "cancel-and-compare", dialog =>
            {
                TestCompactParameters(dialog, pump);
                Control<ComboBox>(dialog, "MaterialSelector").SelectedIndex = (int)GlassMode.Dark;
                Control<Slider>(dialog, "DensitySlider").Value = 0.7;
                Click(dialog, "CompareButton");
                Check(Control<TextBlock>(dialog, "PreviewStatusText").Text == "原外观的示例效果", "对比应切换到原外观示例");
                Check(Control<AppearanceSampleView>(dialog, "SamplePreview").DisplayedAppearance == DesktopStyleRules.Appearance(state.Style),
                    "原效果应使用打开时的外观");
                Click(dialog, "CompareButton");
                Check(Control<AppearanceSampleView>(dialog, "SamplePreview").DisplayedAppearance == DesktopStyleRules.Appearance(dialog.Draft),
                    "返回试选应显示草稿");
                Click(dialog, "CompareButton");
                Control<Slider>(dialog, "DensitySlider").Value = 0.71;
                pump();
                Check(Control<TextBlock>(dialog, "PreviewStatusText").Text == "试选外观的示例效果"
                    && Control<AppearanceSampleView>(dialog, "SamplePreview").DisplayedAppearance == DesktopStyleRules.Appearance(dialog.Draft),
                    "原效果对比中修改参数应立即返回草稿预览");
                Check(!VisualText(dialog).Contains("PRIVATE_APPEARANCE_MARKER", StringComparison.Ordinal), "示例不能显示个人内容");
                Check(JsonSerializer.Serialize(state) == original && calls == 0, "试选和对比不应写回状态或应用");
                Click(dialog, "CancelButton");
                Check(!dialog.IsVisible && dialog.AppliedAppearance is null, "取消应关闭窗口并丢弃草稿");
            });
            Check(JsonSerializer.Serialize(state) == original && calls == 0, "取消不应修改正式状态");

            OpenDialog(window, "native-escape", dialog =>
            {
                Control<ComboBox>(dialog, "MaterialSelector").SelectedIndex = (int)GlassMode.Clear;
                dialog.Activate();
                TopBarWindowTests.ForceForeground(new WindowInteropHelper(dialog).Handle);
                Control<ComboBox>(dialog, "MaterialSelector").Focus();
                pump();
                Check(DockNativeMethods.GetForegroundWindow() == new WindowInteropHelper(dialog).Handle,
                    "真实Esc输入前，试选窗口必须在前台");
                Check(ShellSurfaceNativeMethods.SendChord(Array.Empty<ushort>(), 0x1B), "系统应接受真实Esc按键");
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (dialog.IsVisible && DateTime.UtcNow < deadline) pump();
                Check(!dialog.IsVisible && dialog.AppliedAppearance is null, "真实Esc应关闭窗口并丢弃草稿");
            });
            OpenDialog(window, "escape-open-dropdown", dialog =>
            {
                var selector = Control<ComboBox>(dialog, "MaterialSelector");
                dialog.Activate();
                TopBarWindowTests.ForceForeground(new WindowInteropHelper(dialog).Handle);
                selector.Focus();
                selector.IsDropDownOpen = true;
                pump();
                Check(DockNativeMethods.GetForegroundWindow() == new WindowInteropHelper(dialog).Handle && selector.IsDropDownOpen,
                    "下拉框 Esc 测试应在前台且列表已展开");
                Check(ShellSurfaceNativeMethods.SendChord(Array.Empty<ushort>(), 0x1B), "系统应接受下拉框真实Esc按键");
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (selector.IsDropDownOpen && DateTime.UtcNow < deadline) pump();
                Check(dialog.IsVisible && !selector.IsDropDownOpen, "下拉框展开时Esc应只收起列表，不关闭试选窗口");
            });
            OpenDialog(window, "window-close", dialog =>
            {
                Control<Slider>(dialog, "DensitySlider").Value = 0.9;
                dialog.Close();
            });
            Check(JsonSerializer.Serialize(state) == original && calls == 0, "Esc和标题栏关闭不应写回");
            OpenDialog(window, "unchanged-apply", dialog => Click(dialog, "ApplyButton"));
            Check(JsonSerializer.Serialize(state) == original && calls == 0, "没有修改时应用应为无操作");

            var expected = JsonSerializer.Deserialize<AppState>(original)!;
            expected.Style.Glass = GlassMode.Dark;
            expected.Style.Palette = "mint";
            expected.Style.GlassDensity = 0.77;
            expected.Style.TextInk = TextInk.Custom;
            expected.Style.CustomTextColor = "#FFE08A";
            OpenDialog(window, "apply-five-fields", dialog =>
            {
                var palette = Control<WrapPanel>(dialog, "PalettePanel").Children.OfType<Button>().Single(b => Equals(b.Tag, "mint"));
                palette.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Control<ComboBox>(dialog, "MaterialSelector").SelectedIndex = (int)GlassMode.Dark;
                Control<Slider>(dialog, "DensitySlider").Value = 0.77;
                dialog.Draft.CustomTextColor = "#FFE08A";
                Control<ComboBox>(dialog, "TextInkSelector").SelectedIndex = (int)TextInk.Custom;
                Check(calls == 0 && JsonSerializer.Serialize(state) == original, "应用前应保持正式状态");
                Click(dialog, "ApplyButton");
            });
            pump();
            Check(calls == 1 && JsonSerializer.Serialize(state) == JsonSerializer.Serialize(expected),
                "应用应只提交五项外观字段，一次调用现有应用路径，并保留个人内容及其他样式");
            OpenDialog(window, "reopen-cancel", dialog =>
            {
                Check(CardAppearanceSelection.From(dialog.Draft) == CardAppearanceSelection.From(state.Style), "重新打开应读取已应用值");
                Control<ComboBox>(dialog, "MaterialSelector").SelectedIndex = (int)GlassMode.Clear;
                Click(dialog, "CancelButton");
            });
            Check(calls == 1, "重新打开后取消不应重复应用");

            var oldThumb = Control<Image>(window, "QuietPresetThumb").Source;
            state.DesktopExperience.GetComponent(DesktopComponentKind.Weather).Appearance.BackgroundColor = "#8800AA";
            window.LoadStyle();
            pump();
            var newThumb = Control<Image>(window, "QuietPresetThumb").Source;
            Check(!ReferenceEquals(oldThumb, newThumb) && !Pixels((BitmapSource)oldThumb!).SequenceEqual(Pixels((BitmapSource)newThumb!)),
                "刷新后缩略图应呈现新配色");
            Check(Control<Image>(window, "QuietPresetThumb").Stretch == Stretch.Uniform, "竖屏缩略图不能裁切");

            var beforeParentClose = JsonSerializer.Serialize(state);
            Console.WriteLine("APPEARANCE_TEST owner-close");
            var closingDraft = new AppearancePreviewWindow(state.Style) { Owner = window };
            closingDraft.Show();
            pump();
            Control<ComboBox>(closingDraft, "MaterialSelector").SelectedIndex = (int)GlassMode.Clear;
            window.Close();
            pump();
            Check(!window.IsVisible && !closingDraft.IsVisible && closingDraft.AppliedAppearance is null,
                "父窗口关闭应级联关闭并丢弃草稿");
            Check(calls == 1 && JsonSerializer.Serialize(state) == beforeParentClose, "父窗口关闭不应提交草稿");
        }
        finally
        {
            window.Close();
            pump();
        }

        Console.WriteLine("APPEARANCE_TEST sample-colours");
        TestSampleColours();
        Console.WriteLine("APPEARANCE_TEST preset-ratio");
        TestPresetRatio();
        Console.WriteLine("APPEARANCE_TEST complete");
    }

    private static void TestCompactParameters(AppearancePreviewWindow dialog, Action pump)
    {
        var width = dialog.Width;
        var height = dialog.Height;
        var root = (FrameworkElement)dialog.Content;
        dialog.Width = 560;
        dialog.Height = 460;
        root.LayoutTransform = new ScaleTransform(1.5, 1.5);
        dialog.UpdateLayout();
        pump();
        var scroll = Control<ScrollViewer>(dialog, "ParameterScroll");
        Check(scroll.ViewportHeight >= 80d, "小窗口放大内容时参数区仍应有可用高度");
        var color = Control<Button>(dialog, "CustomColorButton");
        color.BringIntoView();
        dialog.UpdateLayout();
        pump();
        var position = color.TranslatePoint(new Point(), scroll);
        Check(position.Y >= -1d && position.Y + color.ActualHeight <= scroll.ActualHeight + 1d,
            "参数滚动区应能完整显示末尾选色按钮");
        root.LayoutTransform = Transform.Identity;
        dialog.Width = width;
        dialog.Height = height;
        dialog.UpdateLayout();
        pump();
    }

    private static void TestSampleColours()
    {
        var sample = new AppearanceSampleView();
        foreach (var material in Enum.GetValues<GlassMode>())
        foreach (var palette in DesktopStyleRules.Palettes)
        foreach (var ink in Enum.GetValues<TextInk>())
        foreach (var density in new[] { DesktopStyleRules.MinimumGlassDensity, DesktopStyleRules.MaximumGlassDensity })
        {
            var style = new DesktopStyleState { Glass = material, Palette = palette.Key, GlassDensity = density,
                TextInk = ink, CustomTextColor = "#C79BEE" };
            sample.Update(style, lightBackground: density == DesktopStyleRules.MinimumGlassDensity);
            var look = DesktopStyleRules.Appearance(style);
            var canvas = (Canvas)((Viewbox)sample.Content).Child;
            var weather = canvas.Children.OfType<Border>().First();
            var header = ((Grid)weather.Child).Children.OfType<Border>().First();
            Check(weather.Background is SolidColorBrush body && body.Color == Parse(look.BackgroundColor)
                && body.Opacity == look.BackgroundOpacity, "示例卡片底色及浓度应来自现有规则");
            Check(header.Background is SolidColorBrush title && title.Color == Parse(look.HeaderColor)
                && title.Opacity == look.HeaderOpacity, "示例标题底色应来自现有规则");
            Check(sample.DisplayedDock == DesktopStyleRules.Dock(style)
                && canvas.Children.OfType<TextBlock>().First().Foreground is SolidColorBrush text
                && text.Color == Parse(DesktopStyleRules.NakedTextColor(style)), "Dock及裸文字应使用现有规则");
            Check(material != GlassMode.Clear || canvas.Children.OfType<TextBlock>().First().Effect is System.Windows.Media.Effects.DropShadowEffect,
                "无底板时示例文字应带既有柔和阴影");
        }
    }

    private static void TestPresetRatio()
    {
        var state = DesktopExperienceRules.CreateDefault();
        var before = JsonSerializer.Serialize(state);
        foreach (var preset in Enum.GetValues<DesktopLayoutPreset>())
        foreach (var area in new[] { new Rect(-720, -120, 720, 1280), new Rect(0, 0, 1920, 1040) })
        {
            var image = (BitmapSource)ThemePreviewRenderer.RenderPreset(preset, state, area);
            Check(Math.Abs(image.Width / image.Height - area.Width / area.Height) < 0.005, "预设图应保持工作区比例");
        }
        Check(JsonSerializer.Serialize(state) == before, "绘制预设不能修改正式组件状态");
    }

    private static void OpenDialog(SettingsWindow owner, string stage, Action<AppearancePreviewWindow> action)
    {
        Console.WriteLine($"APPEARANCE_TEST {stage}");
        Exception? failure = null;
        AppearancePreviewWindow? active = null;
        var timeout = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(10) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            failure ??= new TimeoutException($"外观试选阶段超时：{stage}");
            foreach (var dialog in owner.OwnedWindows.OfType<AppearancePreviewWindow>().ToArray()) dialog.Close();
        };
        var operation = Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
        {
            try
            {
                active = owner.OwnedWindows.OfType<AppearancePreviewWindow>().Single();
                action(active);
            }
            catch (Exception exception) { failure ??= exception; }
            finally
            {
                if (active?.IsVisible == true) active.Close();
                foreach (var dialog in owner.OwnedWindows.OfType<AppearancePreviewWindow>().ToArray())
                    if (dialog.IsVisible) dialog.Close();
            }
        }));
        timeout.Start();
        try { Click(owner, "AppearancePreviewButton"); }
        finally
        {
            timeout.Stop();
            operation.Abort();
        }
        if (failure is not null) throw new InvalidOperationException($"外观试选阶段失败：{stage}", failure);
    }

    private static byte[] Pixels(BitmapSource source)
    {
        var bytes = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(bytes, source.PixelWidth * 4, 0);
        return bytes;
    }

    private static string VisualText(DependencyObject parent)
    {
        var text = parent is TextBlock block ? block.Text : string.Empty;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            text += VisualText(VisualTreeHelper.GetChild(parent, index));
        return text;
    }

    private static Color Parse(string value) => (Color)ColorConverter.ConvertFromString(value)!;
    private static T Control<T>(Window window, string name) where T : class =>
        window.FindName(name) as T ?? throw new InvalidOperationException($"缺少控件 {name}");
    private static void Click(Window window, string name) => Control<Button>(window, name).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
