using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BingLan.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Orientation = System.Windows.Controls.Orientation;
using UserControl = System.Windows.Controls.UserControl;

namespace BingLan.App.Windows;

/// <summary>Fixed sample content, with no personal state, native windows or data services.</summary>
public sealed class AppearanceSampleView : UserControl
{
    internal GlassAppearance? DisplayedAppearance { get; private set; }
    internal DockGlass? DisplayedDock { get; private set; }
    internal string? DisplayedNakedTextColor { get; private set; }

    public AppearanceSampleView()
    {
        Focusable = false;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "示例预览");
    }

    internal void Update(DesktopStyleState style, bool lightBackground)
    {
        var look = DesktopStyleRules.Appearance(style);
        DisplayedAppearance = look;
        var dock = DesktopStyleRules.Dock(style);
        DisplayedDock = dock;
        var naked = DesktopStyleRules.NakedTextColor(style);
        DisplayedNakedTextColor = naked;
        var canvas = new Canvas { Width = 720, Height = 440, ClipToBounds = true };
        var background = new LinearGradientBrush(
            Parse(lightBackground ? "#E5EDF4" : "#213B58"),
            Parse(lightBackground ? "#B7D2E9" : "#092338"), 35);
        canvas.Background = background;
        var clock = Text("09:41", naked, 54, true);
        var date = Text("10月26日  星期一", naked, 14);
        var textShadow = style.Glass == GlassMode.Clear && style.CardShadow == CardShadow.None
            ? CardShadow.Soft : style.CardShadow;
        clock.Effect = Shadow(textShadow);
        date.Effect = Shadow(textShadow);
        Add(canvas, clock, 32, 24);
        Add(canvas, date, 36, 90);
        Add(canvas, Card("天气", look, style, 286, 111,
            Text("☀  23°  晴", look.TextColor, 27, true),
            Text("最高 26°  ·  最低 18°", look.TextColor, 12, opacity: 0.76)), 400, 26);

        var performance = new StackPanel();
        performance.Children.Add(Text("CPU  24%       RAM  48%", look.TextColor, 14, true));
        performance.Children.Add(new Border
        {
            Height = 4, Margin = new Thickness(0, 9, 0, 9), Background = Brush(style.AccentColor),
            Width = 94, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, CornerRadius = new CornerRadius(2)
        });
        performance.Children.Add(Text("↑ 128 KB/s      ↓ 2.4 MB/s", look.TextColor, 12, opacity: 0.8));
        Add(canvas, Card("电脑状态", look, style, 332, 124, performance), 32, 140);
        Add(canvas, Card("今日待办", look, style, 286, 174,
            Row("□  整理今天的计划", look),
            Row("□  阅读一篇文章", look),
            Text("✓  留一点休息时间", look.TextColor, 12, opacity: 0.6)), 400, 153);

        var files = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, name) in new[] { ("▱", "资料"), ("▤", "笔记"), ("▧", "图片") })
        {
            var tile = new StackPanel { Width = 85 };
            tile.Children.Add(Text(glyph, look.TextColor, 27, true));
            tile.Children.Add(Text(name, look.TextColor, 12));
            files.Children.Add(tile);
        }
        Add(canvas, Card("文件分组", look, style, 332, 112, files), 32, 280);
        var greeting = Text("把今天过得从容一些", naked, 18, true);
        greeting.Effect = Shadow(textShadow);
        Add(canvas, greeting, 415, 347);

        var icons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var glyph in new[] { "◫", "◎", "♫", "✉", "▣" })
        {
            icons.Children.Add(new Border
            {
                Width = 30, Margin = new Thickness(4, 0, 4, 0),
                Child = Text(glyph, look.TextColor, 21, true)
            });
        }
        Add(canvas, new Border
        {
            Background = Brush(dock.Surface), BorderBrush = Brush(dock.Border),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 5, 8, 5), Child = icons
        }, 424, 387);
        Content = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
    }

    private static Border Card(string title, GlassAppearance look, DesktopStyleState style,
        double width, double height, params UIElement[] content)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.Children.Add(new Border
        {
            Background = Brush(look.HeaderColor, look.HeaderOpacity),
            CornerRadius = new CornerRadius(10, 10, 0, 0)
        });
        var titleText = Text(title, look.TextColor, 12, true);
        titleText.Margin = new Thickness(14, 0, 14, 0);
        titleText.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(titleText);
        var body = new StackPanel { Margin = new Thickness(14, 8, 14, 8) };
        foreach (var item in content) body.Children.Add(item);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        var shadow = style.Glass == GlassMode.Clear && style.CardShadow == CardShadow.None
            ? CardShadow.Soft : style.CardShadow;
        titleText.Effect = Shadow(shadow);
        body.Effect = Shadow(shadow);
        return new Border
        {
            Width = width, Height = height, CornerRadius = new CornerRadius(10),
            Background = Brush(look.BackgroundColor, look.BackgroundOpacity), Child = grid
        };
    }

    private static Border Row(string text, GlassAppearance look) => new()
    {
        Background = Brush(look.ItemSurfaceColor), Padding = new Thickness(7, 5, 7, 5),
        Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(5),
        Child = Text(text, look.TextColor, 12)
    };

    private static DropShadowEffect? Shadow(CardShadow shadow) => shadow == CardShadow.None ? null : new()
    {
        Color = Colors.Black, BlurRadius = shadow == CardShadow.Strong ? 10 : 6,
        ShadowDepth = shadow == CardShadow.Strong ? 2 : 1, Opacity = shadow == CardShadow.Strong ? 0.5 : 0.28
    };

    private static TextBlock Text(string text, string color, double size, bool bold = false, double opacity = 1) => new()
    {
        Text = text, Foreground = Brush(color), FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Opacity = opacity,
        TextWrapping = TextWrapping.Wrap
    };

    private static void Add(Canvas canvas, UIElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        canvas.Children.Add(element);
    }

    private static Color Parse(string color) => (Color)ColorConverter.ConvertFromString(color)!;
    private static SolidColorBrush Brush(string color, double opacity = 1) => new(Parse(color)) { Opacity = opacity };
}
