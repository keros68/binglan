using System.Text.Json;
using System.Text.Json.Serialization;

namespace BingLan.Core.Models;

public sealed class LauncherAppearanceState
{
    public const string DefaultSurfaceColor = "#D8F3FF";
    public const double DefaultSurfaceOpacity = 0.46d;
    public const double MinimumSurfaceOpacity = 0.12d;
    public const double DefaultCornerRadius = 24d;

    private string _surfaceColor = DefaultSurfaceColor;
    private double _surfaceOpacity = DefaultSurfaceOpacity;
    private double _cornerRadius = DefaultCornerRadius;

    public bool FollowCardLook { get; set; } = true;
    public bool UseClearGlass { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    public string SurfaceColor
    {
        get => _surfaceColor;
        set => _surfaceColor = WidgetAppearanceRules.CoerceColorOrDefault(value, DefaultSurfaceColor);
    }

    public double SurfaceOpacity
    {
        get => _surfaceOpacity;
        set => _surfaceOpacity = double.IsFinite(value)
            ? Math.Clamp(value, MinimumSurfaceOpacity, 1d) : DefaultSurfaceOpacity;
    }

    public double CornerRadius
    {
        get => _cornerRadius;
        set => _cornerRadius = double.IsFinite(value)
            ? WidgetAppearanceRules.CoerceCornerRadius(value) : DefaultCornerRadius;
    }

    public DockGlass ResolveGlass(DesktopStyleState style)
    {
        var (rgb, opacity) = ResolveSurface(style);
        return DesktopStyleRules.CustomDock(rgb, opacity);
    }

    public (DockGlass Glass, LauncherReadability Readability) ResolveReadableGlass(DesktopStyleState style)
    {
        var (rgb, opacity) = ResolveSurface(style);
        var readability = LauncherReadabilityRules.Resolve(rgb, opacity);
        return (DesktopStyleRules.CustomDock(rgb, readability.SurfaceOpacity), readability);
    }

    private (string Rgb, double Opacity) ResolveSurface(DesktopStyleState style)
    {
        if (!FollowCardLook)
        {
            return (SurfaceColor, SurfaceOpacity);
        }

        var opacity = Math.Clamp(DefaultSurfaceOpacity + (style.GlassDensity - DesktopStyleRules.DefaultGlassDensity) * 0.6d,
            MinimumSurfaceOpacity, 1d);
        var palette = DesktopStyleRules.GetPalette(style.Palette);
        return style.Glass switch
        {
            GlassMode.Dark => ("#18222F", opacity),
            GlassMode.Clear => (DefaultSurfaceColor, MinimumSurfaceOpacity),
            _ => (palette.Key == DesktopStyleRules.DefaultPalette ? DefaultSurfaceColor : palette.Body, opacity)
        };
    }
}
