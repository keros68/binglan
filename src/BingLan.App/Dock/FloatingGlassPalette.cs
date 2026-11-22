using System.Windows;
using System.Windows.Media;
using BingLan.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace BingLan.App.Dock;

internal static class FloatingGlassPalette
{
    internal static void Apply(ResourceDictionary resources, DockGlass dock, bool highContrast = false,
        bool launcher = false, bool? darkInk = null)
    {
        var tint = (Color)ColorConverter.ConvertFromString(dock.Surface);
        var light = darkInk ?? (0.2126d * tint.R + 0.7152d * tint.G + 0.0722d * tint.B) / 255d > 0.6d;
        var solid = tint;
        solid.A = byte.MaxValue;

        Add("FloatingGlassSurfaceBrush", BrushOf(solid), SystemColors.WindowBrush);
        if (launcher)
        {
            Add("FloatingGlassPanelBrush", BrushOf(tint), SystemColors.WindowBrush);
            Add("FloatingGlassLabelBrush", Paint(light ? "#B8F3FAFF" : "#C8182534"), SystemColors.WindowBrush);
            Add("LauncherTileHoverBrush", Paint(light ? "#38FFFFFF" : "#348FB8EB"), SystemColors.ControlBrush);
            Add("FloatingGlassNavigationBrush", Paint(light ? "#29F3FAFF" : "#35182534"), SystemColors.WindowBrush);
            Add("FloatingGlassHeadingBrush", Brushes.Transparent, SystemColors.WindowBrush);
        }
        else
        {
            var capsuleAlpha = (byte)Math.Clamp(tint.A - 38, light ? 166 : 184, 226);
            Add("FloatingGlassCapsuleBrush", BrushOf(Color.FromArgb(capsuleAlpha, tint.R, tint.G, tint.B)), SystemColors.WindowBrush);
        }
        Add("FloatingGlassBorderBrush", Paint(light ? "#D9FFFFFF" : "#58FFFFFF"), SystemColors.WindowTextBrush);
        Add("FloatingGlassInkBrush", Paint(light ? "#122C44" : "#F2F6FC"), SystemColors.WindowTextBrush);
        Add("FloatingGlassMutedBrush", Paint(light ? "#263F58" : "#D0DEED"), SystemColors.WindowTextBrush);
        Add("FloatingGlassControlBrush", Paint(launcher
            ? (light ? "#A4F3FAFF" : "#AA182534")
            : (light ? "#50FFFFFF" : "#22FFFFFF")), SystemColors.WindowBrush);
        Add("FloatingGlassControlBorderBrush", Paint(light ? "#48486681" : "#56FFFFFF"), SystemColors.WindowTextBrush);
        Add("FloatingGlassHoverBrush", Paint(launcher
            ? (light ? "#C2F3FAFF" : "#C7223040")
            : (light ? "#80FFFFFF" : "#38FFFFFF")), SystemColors.ControlBrush);
        Add("FloatingGlassSelectedBrush", Paint(light ? "#AFC0DBF2" : (launcher ? "#CD385A79" : "#665581AC")),
            SystemColors.ControlBrush);
        Add("FloatingGlassFocusBrush", Paint(light ? "#316B9C" : "#AFD5FF"), SystemColors.HighlightBrush);

        void Add(string key, Brush normal, Brush contrast) => resources[key] = highContrast ? contrast : normal;
    }

    private static Brush Paint(string value) => BrushOf((Color)ColorConverter.ConvertFromString(value));

    private static Brush BrushOf(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
