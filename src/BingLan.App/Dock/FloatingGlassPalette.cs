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
        bool launcher = false, bool? darkInk = null, bool clearGlass = false)
    {
        var tint = (Color)ColorConverter.ConvertFromString(dock.Surface);
        var light = darkInk ?? (0.2126d * tint.R + 0.7152d * tint.G + 0.0722d * tint.B) / 255d > 0.6d;
        var solid = tint;
        solid.A = byte.MaxValue;

        if (launcher && clearGlass && !highContrast)
        {
            // Clear-glass overview card keeps an airy light-ice surface with no dark opaque panels
            // and no per-label boxes; text separation comes from subtle drop shadow instead.
            Add("FloatingGlassPanelBrush", BrushOf(tint), SystemColors.WindowBrush);
            Add("FloatingGlassSurfaceBrush", Paint(light ? "#EAF3FAFF" : "#E624384E"), SystemColors.WindowBrush);
            Add("FloatingGlassLabelBrush", Paint(light ? "#52F3FAFF" : "#34FFFFFF"), SystemColors.WindowBrush);
            Add("LauncherTileHoverBrush", Paint(light ? "#48FFFFFF" : "#38FFFFFF"), SystemColors.ControlBrush);
            Add("FloatingGlassNavigationBrush", Paint(light ? "#3CF3FAFF" : "#24FFFFFF"), SystemColors.WindowBrush);
            Add("FloatingGlassHeadingBrush", Brushes.Transparent, SystemColors.WindowBrush);
            Add("FloatingGlassBorderBrush", Paint("#AAFFFFFF"), SystemColors.WindowTextBrush);
            Add("FloatingGlassInkBrush", Paint(light ? "#122C44" : "#FFFFFFFF"), SystemColors.WindowTextBrush);
            Add("FloatingGlassMutedBrush", Paint(light ? "#263F58" : "#E8F4FF"), SystemColors.WindowTextBrush);
            Add("FloatingGlassControlBrush", Paint(light ? "#56F3FAFF" : "#2EFFFFFF"), SystemColors.WindowBrush);
            Add("FloatingGlassControlBorderBrush", Paint(light ? "#665E7D98" : "#62FFFFFF"), SystemColors.WindowTextBrush);
            Add("FloatingGlassHoverBrush", Paint(light ? "#7CF5FBFF" : "#44FFFFFF"), SystemColors.ControlBrush);
            Add("FloatingGlassSelectedBrush", Paint(light ? "#88B6DDF7" : "#5A72BFEF"), SystemColors.ControlBrush);
            Add("FloatingGlassFocusBrush", Paint(light ? "#316B9C" : "#D68CD8FF"), SystemColors.HighlightBrush);
            return;
        }

        Add("FloatingGlassSurfaceBrush", BrushOf(solid), SystemColors.WindowBrush);
        if (launcher)
        {
            Add("FloatingGlassPanelBrush", BrushOf(tint), SystemColors.WindowBrush);
            Add("FloatingGlassLabelBrush", Paint(light ? "#CCF0F9FF" : "#C8182534"), SystemColors.WindowBrush);
            Add("LauncherTileHoverBrush", Paint(light ? "#48FFFFFF" : "#348FB8EB"), SystemColors.ControlBrush);
            Add("FloatingGlassNavigationBrush", Paint(light ? "#58F3FAFF" : "#35182534"), SystemColors.WindowBrush);
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
            ? (light ? "#B4F3FAFF" : "#AA182534")
            : (light ? "#50FFFFFF" : "#22FFFFFF")), SystemColors.WindowBrush);
        Add("FloatingGlassControlBorderBrush", Paint(light ? "#48486681" : "#56FFFFFF"), SystemColors.WindowTextBrush);
        Add("FloatingGlassHoverBrush", Paint(launcher
            ? (light ? "#D8F5FBFF" : "#C7223040")
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
