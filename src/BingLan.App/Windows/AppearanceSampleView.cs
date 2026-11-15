using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Orientation = System.Windows.Controls.Orientation;
using Path = System.Windows.Shapes.Path;
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
        var rendered = DesktopStyleRules.ResolveRenderedCard(style);
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
        var weatherHeadline = new StackPanel { Orientation = Orientation.Horizontal };
        weatherHeadline.Children.Add(new Path
        {
            Data = Geometry.Parse(WeatherIconRules.GetPathData(WeatherIconKind.Clear)),
            Stroke = Brush(look.TextColor),
            StrokeThickness = 1.8,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 22,
            Height = 22,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        });
        weatherHeadline.Children.Add(Text("23°  晴", look.TextColor, 27, true));
        Add(canvas, Card("天气", look, rendered, 286, 111,
            weatherHeadline,
            Text("最高 26°  ·  最低 18°", look.TextColor, 12, opacity: 0.76)), 400, 26);

        var performance = new StackPanel();
        performance.Children.Add(Text("CPU  24%       RAM  48%", look.TextColor, 14, true));
        performance.Children.Add(new Border
        {
            Height = 4, Margin = new Thickness(0, 9, 0, 9), Background = Brush(style.AccentColor),
            Width = 94, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, CornerRadius = new CornerRadius(2)
        });
        performance.Children.Add(Text("↑ 128 KB/s      ↓ 2.4 MB/s", look.TextColor, 12, opacity: 0.8));
        Add(canvas, Card("电脑状态", look, rendered, 332, 124, performance), 32, 140);
        Add(canvas, Card("今日待办", look, rendered, 286, 174,
            Row("□  整理今天的计划", look, rendered),
            Row("□  阅读一篇文章", look, rendered),
            Text("✓  留一点休息时间", look.TextColor, 12, opacity: 0.6)), 400, 153);

        var files = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (glyph, name) in new[] { ("▱", "资料"), ("▤", "笔记"), ("▧", "图片") })
        {
            var tile = new StackPanel { Width = 85 };
            tile.Children.Add(Text(glyph, look.TextColor, 27, true));
            tile.Children.Add(Text(name, look.TextColor, 12));
            files.Children.Add(tile);
        }
        Add(canvas, Card("文件分组", look, rendered, 332, 112, files), 32, 280);
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

    private static Border Card(string title, GlassAppearance look, RenderedCardLook rendered,
        double width, double height, params UIElement[] content)
    {
        var isClear = rendered.BackgroundOpacity <= WidgetAppearanceRules.MinimumBackgroundOpacity + 0.005d;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(34) });
        grid.RowDefinitions.Add(new RowDefinition());
        // Keep the accent tag as the first Border child so AppearancePreviewTests finds the header swatch directly.
        grid.Children.Add(new Border
        {
            Width = 4,
            Height = 14,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
            CornerRadius = new CornerRadius(2),
            Background = Brush(look.HeaderColor, look.HeaderOpacity)
        });
        var titleText = Text(title, look.TextColor, 12, true);
        titleText.Margin = new Thickness(22, 0, 14, 0);
        titleText.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(titleText);
        grid.Children.Add(new Border
        {
            Height = 1,
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = isClear
                ? System.Windows.Media.Brushes.Transparent
                : Brush(look.TextColor, 0.14)
        });
        var body = new StackPanel { Margin = new Thickness(14, 6, 14, 8) };
        foreach (var item in content) body.Children.Add(item);
        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        titleText.Effect = Shadow(rendered.EffectiveShadow);
        body.Effect = Shadow(rendered.EffectiveShadow);
        var isDark = ColorContrastRules.RelativeLuminance(rendered.BackgroundColor) < 0.25d;
        Brush borderBrush = isClear
            ? System.Windows.Media.Brushes.Transparent
            : new LinearGradientBrush(
                Parse(isDark ? "#38FFFFFF" : "#99FFFFFF"),
                Parse(isDark ? "#18FFFFFF" : "#4DFFFFFF"),
                new System.Windows.Point(0, 0),
                new System.Windows.Point(0, 1));
        return new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(12),
            Background = Brush(rendered.BackgroundColor, rendered.BackgroundOpacity),
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(isClear ? 0 : 1),
            Child = grid
        };
    }

    private static Border Row(string text, GlassAppearance look, RenderedCardLook rendered) => new()
    {
        Background = Brush(rendered.ItemSurfaceColor), Padding = new Thickness(7, 5, 7, 5),
        Margin = new Thickness(0, 0, 0, 5), CornerRadius = new CornerRadius(8),
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
