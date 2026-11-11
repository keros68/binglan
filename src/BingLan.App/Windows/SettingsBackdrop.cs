using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using BingLan.App.Interop;
using BingLan.App.Taskbar;
using Brushes = System.Windows.Media.Brushes;
using SystemColors = System.Windows.SystemColors;

namespace BingLan.App.Windows;

/// <summary>
/// Gives the settings window the Windows 11 Mica material, so the wallpaper tints it the
/// way it tints the desktop cards, and a dark title bar and dark Mica in the dark app mode.
/// Where Mica is missing — before Windows 11 22H2, with transparency effects turned off,
/// or in high contrast — the solid page colour stays.
/// </summary>
internal static class SettingsBackdrop
{
    // First build that accepts DWMWA_SYSTEMBACKDROP_TYPE.
    private const int MinimumBuild = 22621;

    /// <summary>Applies or removes the material; returns whether Mica is now in use.</summary>
    internal static bool Apply(Window window, bool dark)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source)
        {
            return false;
        }

        // Older builds ignore the attribute and keep the light frame.
        var darkFrame = dark && !AccessibilityThemeManager.IsHighContrastEnabled ? 1 : 0;
        _ = NativeMethods.DwmSetWindowAttribute(
            source.Handle,
            NativeMethods.DwmwaUseImmersiveDarkMode,
            ref darkFrame,
            Marshal.SizeOf<int>());

        var wanted = Environment.OSVersion.Version.Build >= MinimumBuild
            && !AccessibilityThemeManager.IsHighContrastEnabled
            && TaskbarAdapter.IsSystemTransparencyEnabled();
        var backdrop = wanted ? NativeMethods.DwmSystemBackdropMainWindow : NativeMethods.DwmSystemBackdropNone;
        var applied = NativeMethods.DwmSetWindowAttribute(
            source.Handle,
            NativeMethods.DwmwaSystemBackdropType,
            ref backdrop,
            Marshal.SizeOf<int>()) == 0;
        var active = wanted && applied;

        var frame = active
            ? new NativeMethods.Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 }
            : new NativeMethods.Margins();
        _ = NativeMethods.DwmExtendFrameIntoClientArea(source.Handle, ref frame);
        if (active)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            window.Background = Brushes.Transparent;
        }
        else
        {
            source.CompositionTarget.BackgroundColor = SystemColors.WindowColor;
            window.SetResourceReference(Window.BackgroundProperty, "IcePageBrush");
        }
        return active;
    }
}
