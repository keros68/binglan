using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

internal static class Program
{
    private const int WmNcHitTest = 0x0084;
    private const int HtClient = 1;
    private const int HtCaption = 2;

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);

    [STAThread]
    private static int Main()
    {
        var failures = new List<string>();
        AppContext.SetSwitch(BingLan.App.App.SuppressCoordinatorStartupSwitch, true);
        var app = new BingLan.App.App();
        app.InitializeComponent();

        Run("便签默认状态与标题正文提交", TestNoteStateAndCommit, failures);
        Run("正文排版规则钳制与回退", TestTypographyRules, failures);
        Run("便签状态 JSON 往返", TestStateJsonRoundTrip, failures);
        Run("便签真实窗口创建编辑与关闭", TestNoteWindowEditing, failures);
        Run("去抖变更通知与失焦立即提交", TestDebounceAndLostFocusCommit, failures);
        Run("正文排版控件写回独立状态", TestTypographyControls, failures);
        Run("锁定后不可缩放但仍可编辑", TestLockBehavior, failures);

        if (failures.Count > 0)
        {
            Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
            return 1;
        }

        Console.WriteLine("全部便签测试通过。");
        return 0;
    }

    private static void TestNoteStateAndCommit()
    {
        var first = NoteService.CreateDefaultWidget();
        var second = NoteService.CreateDefaultWidget();
        Assert(first.Id != second.Id, "多个便签实例必须拥有独立 ID");
        Assert(first.Title == "便签" && first.Content == "", "默认便签标题或正文错误");
        Assert(!first.IsLocked, "默认便签不应锁定");
        Assert(first.BodyTypography.FontFamily == NoteBodyTypographyRules.DefaultFontFamily &&
               first.BodyTypography.FontSize == NoteBodyTypographyRules.DefaultFontSize &&
               first.BodyTypography.Color == NoteBodyTypographyRules.DefaultColor &&
               !first.BodyTypography.Bold &&
               !first.BodyTypography.Italic &&
               first.BodyTypography.Alignment == NoteBodyTextAlignment.Left,
            "正文排版默认值错误");

        NoteService.CommitTitle(first, "  购物清单  ");
        Assert(first.Title == "购物清单", "标题提交未去除首尾空白");
        NoteService.CommitTitle(first, "   ");
        Assert(first.Title == NoteService.DefaultTitle, "空白标题未回退默认标题");

        NoteService.CommitContent(first, "第一行\r\n第二行\r第三行\n第四行");
        Assert(first.Content == "第一行\n第二行\n第三行\n第四行", "多行正文换行未保留或规范化");
        NoteService.CommitContent(first, null);
        Assert(first.Content == "", "空正文提交错误");
        Assert(NoteService.ToEditorText("甲\n乙") == $"甲{Environment.NewLine}乙",
            "编辑器文本未转换为平台换行");
    }

    private static void TestTypographyRules()
    {
        var typography = new NoteBodyTypography();
        typography.FontSize = 200d;
        Assert(typography.FontSize == NoteBodyTypographyRules.MaximumFontSize, "过大字号未钳制");
        typography.FontSize = 1d;
        Assert(typography.FontSize == NoteBodyTypographyRules.MinimumFontSize, "过小字号未钳制");
        typography.FontSize = double.NaN;
        Assert(typography.FontSize == NoteBodyTypographyRules.DefaultFontSize, "NaN 字号未回退默认值");

        typography.Color = "#a1b2c3";
        Assert(typography.Color == "#A1B2C3", "正文颜色未规范为大写 #RRGGBB");
        typography.Color = "#12345";
        Assert(typography.Color == NoteBodyTypographyRules.DefaultColor, "非法正文颜色未回退默认值");
        typography.Color = null!;
        Assert(typography.Color == NoteBodyTypographyRules.DefaultColor, "空正文颜色未回退默认值");

        typography.FontFamily = "  Segoe UI  ";
        Assert(typography.FontFamily == "Segoe UI", "正文字体名未去除首尾空白");
        typography.FontFamily = "Bad\nFont";
        Assert(typography.FontFamily == NoteBodyTypographyRules.DefaultFontFamily,
            "含控制字符的正文字体名未回退默认值");

        typography.Alignment = NoteBodyTextAlignment.Right;
        Assert(typography.Alignment == NoteBodyTextAlignment.Right, "对齐设置错误");
    }

    private static void TestStateJsonRoundTrip()
    {
        var state = new NoteWidgetState
        {
            Title = "周末计划",
            IsLocked = true,
            CornerRadius = 3d,
            Placement = new WindowPlacement { Left = 111, Top = 122, Width = 333, Height = 244 }
        };
        NoteService.CommitContent(state, "第一行\r\n第二行\n第三行");
        state.Appearance.BackgroundColor = "#C1D2E3";
        state.Appearance.BackgroundOpacity = 0.45;
        state.Appearance.TitleFontFamily = "Segoe UI";
        state.BodyTypography.FontFamily = "Segoe UI";
        state.BodyTypography.FontSize = 18d;
        state.BodyTypography.Color = "#10203A";
        state.BodyTypography.Bold = true;
        state.BodyTypography.Italic = true;
        state.BodyTypography.Alignment = NoteBodyTextAlignment.Center;

        var json = JsonSerializer.Serialize(state);
        var restored = JsonSerializer.Deserialize<NoteWidgetState>(json)!;

        Assert(restored.Id == state.Id, "便签 ID 未往返");
        Assert(restored.Title == "周末计划", "便签标题未往返");
        Assert(restored.Content == "第一行\n第二行\n第三行", "多行正文未完整往返");
        Assert(restored.IsLocked, "锁定状态未往返");
        Assert(restored.CornerRadius == 3d, "圆角未往返");
        Assert(restored.Placement.Left == 111 && restored.Placement.Width == 333, "位置尺寸未往返");
        Assert(restored.Appearance.BackgroundColor == "#C1D2E3" &&
               restored.Appearance.BackgroundOpacity == 0.45,
            "材质外观未往返");
        Assert(restored.BodyTypography.FontFamily == "Segoe UI" &&
               restored.BodyTypography.FontSize == 18d &&
               restored.BodyTypography.Color == "#10203A" &&
               restored.BodyTypography.Bold &&
               restored.BodyTypography.Italic &&
               restored.BodyTypography.Alignment == NoteBodyTextAlignment.Center,
            "正文排版未往返");
    }

    private static void TestNoteWindowEditing()
    {
        var state = new NoteWidgetState();
        var window = new NoteWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var title = Require<TextBox>(window, "TitleEditor");
            var body = Require<TextBox>(window, "BodyEditor");
            Assert(title.Text == "便签", "标题编辑框没有采用默认标题");
            Assert(body.Text == "", "正文编辑框应为空");
            Assert(body.AcceptsReturn && body.TextWrapping == TextWrapping.Wrap,
                "正文编辑框必须支持多行并自动换行");
            Assert(!FindVisualChildren<CheckBox>(window).Any(),
                "便签不得出现待办复选框");
            Assert(!FindVisualChildren<TextBlock>(window)
                    .Any(text => text.Text.Contains("已完成")),
                "便签不得出现完成数等待办语义");
            Assert(!window.ContextMenu!.Items.OfType<MenuItem>()
                    .Any(item => item.Header?.ToString()?.Contains("隐藏已完成") == true),
                "便签右键菜单不得出现隐藏已完成语义");

            AssertAppearanceBrush(
                window.WidgetBackgroundBrush,
                Color.FromArgb(82, 255, 255, 255),
                "便签默认主体材质");
            AssertAppearanceBrush(
                window.WidgetHeaderBrush,
                Color.FromArgb(247, 160, 180, 225),
                "便签默认标题材质");
            Assert(title.FontFamily.Source == WidgetAppearanceRules.DefaultTitleFontFamily &&
                   title.FontWeight == FontWeights.Bold,
                "便签标题没有复用冰蓝标题字体");
            window.WidgetCornerRadius = 3d;
            Pump();
            Assert(Require<Border>(window, "Surface").CornerRadius == new CornerRadius(3d),
                "便签没有复用宿主圆角");

            Assert(title.IsReadOnly && !window.IsTitleEditing, "未编辑时标题应是拖动区域");
            window.BeginTitleEdit();
            Assert(window.IsTitleEditing && !title.IsReadOnly, "标题没有进入编辑");
            title.Text = "我的便签";
            window.EndTitleEdit(commit: true);
            Pump();
            Assert(state.Title == "我的便签" && !window.IsTitleEditing, "标题编辑未写回状态");

            body.Text = "第一行\r\n第二行";
            body.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Pump();
            Assert(state.Content == "第一行\n第二行", "多行正文编辑未写回状态或丢失换行");

            window.Left = 321;
            window.Top = 234;
            window.Width = 345;
            window.Height = 256;
            Pump();
            window.CaptureState();
            Assert(Near(state.Placement.Left, 321) && Near(state.Placement.Top, 234) &&
                   Near(state.Placement.Width, 345) && Near(state.Placement.Height, 256),
                $"位置尺寸未写回状态：实际 Left={state.Placement.Left} Top={state.Placement.Top} Width={state.Placement.Width} Height={state.Placement.Height}");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestDebounceAndLostFocusCommit()
    {
        var state = new NoteWidgetState();
        var window = new NoteWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var body = Require<TextBox>(window, "BodyEditor");
            var notified = false;
            window.WidgetChanged += _ => notified = true;

            var watch = Stopwatch.StartNew();
            body.Text = "去抖第一行\n去抖第二行";
            Pump();
            Assert(!notified, "文本变更在去抖窗口内不应立即通知");
            Assert(state.Content == "", "文本变更在去抖窗口内不应立即提交");
            PumpUntil(() => notified, "去抖计时结束后没有发出变更通知");
            Assert(watch.Elapsed >= TimeSpan.FromMilliseconds(350),
                $"去抖间隔小于 350ms：实际 {watch.ElapsedMilliseconds}ms");
            Assert(watch.Elapsed <= TimeSpan.FromSeconds(5),
                "去抖通知明显超时");
            Assert(state.Content == "去抖第一行\n去抖第二行", "去抖提交后正文内容错误");

            notified = false;
            var focusWatch = Stopwatch.StartNew();
            body.Text = "失焦立即提交";
            body.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Assert(notified && state.Content == "失焦立即提交",
                "失焦没有立即提交并通知");
            Assert(focusWatch.Elapsed < TimeSpan.FromMilliseconds(350),
                "失焦提交仍在等待去抖计时");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestTypographyControls()
    {
        var state = new NoteWidgetState();
        var window = new NoteWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var body = Require<TextBox>(window, "BodyEditor");
            var formatBar = Require<Border>(window, "FormatBar");
            Assert(formatBar.Visibility == Visibility.Collapsed, "正文样式栏默认不应展开");
            var formatToggle = Require<Button>(window, "FormatToggle");
            Assert(formatToggle.Visibility == Visibility.Collapsed, "未选中便签时不应显示正文样式入口");
            window.ApplySelection(true);
            Pump();
            Assert(formatToggle.Visibility == Visibility.Visible, "选中便签后应显示正文样式入口");
            formatToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(formatBar.Visibility == Visibility.Visible, "Aa 按钮没有展开正文样式栏");

            var boldToggle = Require<ToggleButton>(window, "BodyBoldToggle");
            boldToggle.IsChecked = true;
            boldToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            var italicToggle = Require<ToggleButton>(window, "BodyItalicToggle");
            italicToggle.IsChecked = true;
            italicToggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(state.BodyTypography.Bold && state.BodyTypography.Italic,
                "粗体斜体未写回状态");
            Assert(body.FontWeight == FontWeights.Bold && body.FontStyle == FontStyles.Italic,
                "粗体斜体未应用到可见正文");

            var alignRight = Require<ToggleButton>(window, "AlignRightToggle");
            alignRight.IsChecked = true;
            alignRight.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(state.BodyTypography.Alignment == NoteBodyTextAlignment.Right,
                "右对齐未写回状态");
            Assert(body.TextAlignment == TextAlignment.Right, "右对齐未应用到可见正文");
            Assert(Require<ToggleButton>(window, "AlignLeftToggle").IsChecked == false,
                "对齐按钮没有互斥");

            var alignCenter = Require<ToggleButton>(window, "AlignCenterToggle");
            alignCenter.IsChecked = true;
            alignCenter.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Pump();
            Assert(state.BodyTypography.Alignment == NoteBodyTextAlignment.Center &&
                   alignRight.IsChecked == false,
                "居中对齐未写回状态或未取消右对齐");

            var fontPicker = Require<ComboBox>(window, "BodyFontPicker");
            Assert(fontPicker.Items.OfType<string>().Contains("Segoe UI"),
                "正文字体选择器没有列出 Segoe UI");
            fontPicker.SelectedItem = "Segoe UI";
            Pump();
            Assert(state.BodyTypography.FontFamily == "Segoe UI", "正文字体未写回状态");
            Assert(body.FontFamily.Source == "Segoe UI", "正文字体未应用到可见正文");

            var sizePicker = Require<ComboBox>(window, "BodyFontSizePicker");
            sizePicker.Text = "18";
            sizePicker.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Pump();
            Assert(state.BodyTypography.FontSize == 18d, "字号未写回状态");
            Assert(body.FontSize == 18d, "字号未应用到可见正文");
            sizePicker.Text = "300";
            sizePicker.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Pump();
            Assert(state.BodyTypography.FontSize == NoteBodyTypographyRules.MaximumFontSize,
                "过大字号未在写回时钳制");

            var colorEditor = Require<TextBox>(window, "BodyColorEditor");
            colorEditor.Text = "#10203a";
            colorEditor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Pump();
            Assert(state.BodyTypography.Color == "#10203A", "正文颜色未写回状态");
            var bodyBrush = body.Foreground as SolidColorBrush ??
                throw new InvalidOperationException("正文颜色不是纯色 Brush");
            Assert(bodyBrush.Color == Color.FromRgb(0x10, 0x20, 0x3A),
                "正文颜色未应用到可见正文");

            window.CaptureState();
            var json = JsonSerializer.Serialize(state);
            var restored = JsonSerializer.Deserialize<NoteWidgetState>(json)!;
            Assert(restored.BodyTypography.Bold && restored.BodyTypography.Italic &&
                   restored.BodyTypography.Alignment == NoteBodyTextAlignment.Center &&
                   restored.BodyTypography.FontFamily == "Segoe UI" &&
                   restored.BodyTypography.FontSize == 48d &&
                   restored.BodyTypography.Color == "#10203A",
                "正文排版状态没有随便签状态整体持久化");
        }
        finally
        {
            Close(window);
        }
    }

    private static void TestLockBehavior()
    {
        var state = new NoteWidgetState
        {
            Placement = new WindowPlacement { Left = 150, Top = 120, Width = 310, Height = 300 }
        };
        var window = new NoteWidgetWindow(state);
        ShowAndPump(window);
        try
        {
            var edgePoint = new Point(window.ActualWidth - 2, window.ActualHeight / 2);
            var edge = window.PointToScreen(edgePoint);
            Assert(HitTest(window, edge) == HtClient && window.CanStartDragAt(edgePoint),
                "普通状态右边缘应允许拖动且不触发缩放");

            state.IsLocked = true;
            window.ApplyPlacement(state.Placement, state.IsLocked);
            Pump();
            Assert(window.IsWidgetLocked && state.IsLocked, "锁定状态未同步到窗口和模型");
            edge = window.PointToScreen(
                new Point(window.ActualWidth - 2, window.ActualHeight / 2));
            Assert(HitTest(window, edge) == HtClient && !window.CanStartDragAt(edgePoint),
                "锁定后边缘不应再拖动卡片");

            var body = Require<TextBox>(window, "BodyEditor");
            body.Text = "锁定后仍可编辑";
            body.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Pump();
            Assert(state.Content == "锁定后仍可编辑", "锁定后正文不应失去编辑能力");
            Assert(body.IsEnabled && !body.IsReadOnly, "锁定后正文编辑框不应被禁用");
        }
        finally
        {
            Close(window);
        }
    }

    private static int HitTest(Window window, Point screenPoint)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var x = (int)Math.Round(screenPoint.X);
        var y = (int)Math.Round(screenPoint.Y);
        return SendMessage(handle, WmNcHitTest, 0, (nint)((y << 16) | (x & 0xFFFF))).ToInt32();
    }

    private static void ShowAndPump(Window window)
    {
        window.Show();
        Pump();
        Assert(window.IsVisible && new WindowInteropHelper(window).Handle != 0, "窗口未真实创建");
    }

    private static void Close(WidgetWindowBase window)
    {
        window.CanClose = true;
        window.Close();
        Pump();
        Assert(!window.IsVisible, "便签窗口未能关闭");
    }

    private static void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new DispatcherOperationCallback(value =>
            {
                ((DispatcherFrame)value!).Continue = false;
                return null;
            }),
            frame);
        Dispatcher.PushFrame(frame);
    }

    private static void PumpUntil(Func<bool> condition, string failureMessage)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(10);
        }
        Assert(condition(), failureMessage);
    }

    private static T Require<T>(FrameworkElement root, string name) where T : FrameworkElement =>
        root.FindName(name) as T ?? throw new InvalidOperationException($"找不到控件 {name}");

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result)
            {
                yield return result;
            }
            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static void AssertAppearanceBrush(Brush value, Color expected, string name)
    {
        var brush = value as SolidColorBrush ??
            throw new InvalidOperationException($"{name}不是纯色 Brush");
        Assert(brush.Color == expected,
            $"{name}颜色错误：实际 {brush.Color}，预期 {expected}");
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) < 0.5;

    private static void Run(string name, Action test, List<string> failures)
    {
        try
        {
            test();
            Console.WriteLine($"通过：{name}");
        }
        catch (Exception ex)
        {
            failures.Add($"失败：{name} - {ex}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
