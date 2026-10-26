using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FlowDirection = System.Windows.FlowDirection;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace BingLan.App.Themes;

/// <summary>
/// Renders a schematic preview of a theme package: an ice-blue canvas with each visible
/// component drawn as a rectangle at its relative layout position, using its background
/// color/opacity and corner radius, plus a dock bar when the dock is enabled. This is
/// never a screenshot — it only draws shapes, colors and generic component labels already
/// stored in the (personal-data-free) theme package, so it cannot leak user content.
/// </summary>
internal static class ThemePreviewRenderer
{
    private const int Width = 640;
    private const int Height = 360;
    private const double Dpi = 96d;

    internal static byte[] Render(ThemePackage package) => Render(package, Width, Height);

    private static byte[] Render(ThemePackage package, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(package);

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(
                new LinearGradientBrush(
                    Color.FromRgb(0xBF, 0xD9, 0xF2),
                    Color.FromRgb(0x7C, 0xA7, 0xD9),
                    new Point(0, 0),
                    new Point(1, 1)),
                null,
                new Rect(0, 0, width, height));

            DrawComponents(context, package, width, height);

            if (package.Bindings.DockEnabled)
            {
                DrawDock(context, width, height);
            }
        }

        var bitmap = new RenderTargetBitmap(width, height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Draws what a layout preset will look like: the given cards' current appearance at
    /// the positions the preset uses on the primary work area.
    /// </summary>
    internal static ImageSource RenderPreset(DesktopLayoutPreset preset, DesktopExperienceState current)
    {
        return RenderPreset(preset, current, SystemParameters.WorkArea);
    }

    internal static ImageSource RenderPreset(DesktopLayoutPreset preset, DesktopExperienceState current, Rect area)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (area.Width <= 0 || area.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(area));
        }
        var state = LocalStateStore.CreateDefault();
        state.DesktopExperience = System.Text.Json.JsonSerializer.Deserialize<DesktopExperienceState>(
            System.Text.Json.JsonSerializer.Serialize(current))!;
        state.DesktopExperience.ActivePreset = preset;
        foreach (var (kind, placement) in DesktopExperienceRules.PresetPlacements(
                     preset, area.Left, area.Top, area.Right, area.Bottom))
        {
            state.DesktopExperience.GetComponent(kind).Placement = placement;
        }
        state.Dock.PinnedApps.Clear();

        var package = ThemeRules.Export(
            state,
            DesktopExperienceRules.GetPresetName(preset),
            new ThemeArea(area.Left, area.Top, area.Width, area.Height),
            new Version(0, 1, 0));
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        var scale = Math.Min(Width / area.Width, Height / area.Height);
        image.StreamSource = new MemoryStream(Render(package,
            Math.Max(1, (int)Math.Round(area.Width * scale)),
            Math.Max(1, (int)Math.Round(area.Height * scale))));
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void DrawComponents(DrawingContext context, ThemePackage package, int width, int height)
    {
        var tokensByKind = package.Tokens.Components.ToDictionary(component => component.Kind);
        foreach (var placement in package.Layout.Components)
        {
            if (!tokensByKind.TryGetValue(placement.Kind, out var tokens) || !tokens.IsVisible)
            {
                continue;
            }

            var bounds = new Rect(
                placement.X * width,
                placement.Y * height,
                Math.Max(4d, placement.Width * width),
                Math.Max(4d, placement.Height * height));

            var fill = ParseColor(tokens.Appearance.BackgroundColor, Colors.White);
            fill.A = (byte)Math.Clamp(tokens.Appearance.BackgroundOpacity * 255d, 0d, 255d);
            var pen = new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 1d);
            context.DrawRoundedRectangle(
                new SolidColorBrush(fill), pen, bounds, tokens.CornerRadius, tokens.CornerRadius);

            var label = ComponentLabel(placement.Kind);
            if (label.Length > 0 && bounds.Width > 40 && bounds.Height > 20)
            {
                var text = new FormattedText(
                    label,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"),
                    12d,
                    new SolidColorBrush(ParseColor(tokens.Appearance.TextColor, Colors.White)),
                    1d);
                context.DrawText(text, new Point(bounds.Left + 8, bounds.Top + 6));
            }
        }
    }

    private static void DrawDock(DrawingContext context, int width, int height)
    {
        const double dockHeight = 20d;
        var dockWidth = width * 0.42;
        var bounds = new Rect((width - dockWidth) / 2, height - dockHeight - 10, dockWidth, dockHeight);
        context.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
            null,
            bounds,
            dockHeight / 2,
            dockHeight / 2);
    }

    private static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return (Color)ColorConverter.ConvertFromString(value)!;
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private static string ComponentLabel(DesktopComponentKind kind) => kind switch
    {
        DesktopComponentKind.TimeDate => "时间",
        DesktopComponentKind.Greeting => "问候",
        DesktopComponentKind.Weather => "天气",
        DesktopComponentKind.Performance => "性能",
        DesktopComponentKind.Todo => "待办",
        DesktopComponentKind.AudioVisualizer => "音频",
        DesktopComponentKind.QuickLaunch => "快捷",
        _ => string.Empty
    };
}
