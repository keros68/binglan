using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using SystemColors = System.Windows.SystemColors;

namespace BingLan.App;

internal static class AccessibilityThemeManager
{
    private static ResourceDictionary? _resources;
    private static bool _subscribed;

    internal static event EventHandler? HighContrastChanged;

    internal static bool IsHighContrastEnabled { get; private set; }

    internal static void EnsureInitialized()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        // Every card calls this. The palette is written once per resource dictionary, so
        // a new card does not put back default brushes over the dock and card colours the
        // app set since; a high-contrast switch rewrites it and tells the app to reapply.
        if (ReferenceEquals(_resources, resources))
        {
            return;
        }
        _resources = resources;
        if (!_subscribed)
        {
            SystemParameters.StaticPropertyChanged += SystemParameters_StaticPropertyChanged;
            _subscribed = true;
        }
        Apply(resources, SystemParameters.HighContrast);
    }

    internal static void SetHighContrastForTesting(bool enabled)
    {
        EnsureInitialized();
        if (_resources is not null)
        {
            Apply(_resources, enabled);
        }
    }

    internal static void RestoreSystemModeForTesting()
    {
        EnsureInitialized();
        if (_resources is not null)
        {
            Apply(_resources, SystemParameters.HighContrast);
        }
    }

    private static void SystemParameters_StaticPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast) && _resources is not null)
        {
            Apply(_resources, SystemParameters.HighContrast);
        }
    }

    private static void Apply(ResourceDictionary resources, bool highContrast)
    {
        if (highContrast)
        {
            ApplyHighContrastPalette(resources);
        }
        else
        {
            ApplyIcePalette(resources);
        }

        if (IsHighContrastEnabled == highContrast)
        {
            return;
        }

        IsHighContrastEnabled = highContrast;
        HighContrastChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyHighContrastPalette(ResourceDictionary resources)
    {
        Set(resources, "PrimaryTextBrush", SystemColors.WindowTextBrush);
        Set(resources, "SecondaryTextBrush", SystemColors.WindowTextBrush);
        Set(resources, "AccentBrush", SystemColors.HighlightBrush);
        Set(resources, "IcePageBrush", SystemColors.WindowBrush);
        Set(resources, "IcePanelBrush", SystemColors.WindowBrush);
        Set(resources, "IcePanelStrokeBrush", SystemColors.WindowTextBrush);
        Set(resources, "IceInkBrush", SystemColors.WindowTextBrush);
        Set(resources, "IceMutedInkBrush", SystemColors.WindowTextBrush);
        Set(resources, "IceActionBrush", SystemColors.HighlightBrush);
        Set(resources, "IceActionTextBrush", SystemColors.HighlightTextBrush);
        Set(resources, "SettingsInputBrush", SystemColors.WindowBrush);
        Set(resources, "SettingsInputBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsButtonBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsButtonBorderBrush", SystemColors.ControlTextBrush);
        Set(resources, "SettingsNavigationBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsStrokeBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsPreviewBrush", SystemColors.WindowBrush);
        Set(resources, "SettingsCardBrush", SystemColors.WindowBrush);
        Set(resources, "SettingsPanelBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsCardBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsCardHoverBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsCardSelectedBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsCardSelectedTextBrush", SystemColors.HighlightTextBrush);
        Set(resources, "SettingsCardSelectedBorderBrush", SystemColors.HighlightTextBrush);
        Set(resources, "SettingsPreviewBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsComponentNavigationBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsComponentBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsBadgeBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsBadgeTextBrush", SystemColors.ControlTextBrush);
        Set(resources, "SettingsResultBrush", SystemColors.WindowBrush);
        Set(resources, "SettingsResultBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsCoordinateTextBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsSurfaceBrush", SystemColors.WindowBrush);
        Set(resources, "SettingsSurfaceHoverBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsSurfaceBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsControlBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsControlHoverBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsControlBorderBrush", SystemColors.ControlTextBrush);
        Set(resources, "SettingsToggleOffBrush", SystemColors.WindowTextBrush);
        Set(resources, "SettingsTrackBrush", SystemColors.ControlTextBrush);
        Set(resources, "SettingsSegmentTrackBrush", SystemColors.ControlBrush);
        Set(resources, "SettingsNavSelectedBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsNavSelectedTextBrush", SystemColors.HighlightTextBrush);
        Set(resources, "SettingsChoiceSelectedBrush", SystemColors.WindowBrush);
        Set(resources, "SettingsIconBlueBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsIconIndigoBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsIconPurpleBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsIconPinkBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsIconOrangeBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsIconGreenBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsIconTealBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsIconGrayBrush", SystemColors.HighlightBrush);
        Set(resources, "SettingsIconGlyphBrush", SystemColors.HighlightTextBrush);
        Set(resources, "DockSurfaceBrush", SystemColors.WindowBrush);
        Set(resources, "DockHandleBrush", SystemColors.WindowBrush);
        Set(resources, "DockSurfaceBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "DockActiveDotBrush", SystemColors.HighlightBrush);
        Set(resources, "PerformanceAccentBrush", SystemColors.HighlightBrush);
        Set(resources, "TodoDueSoonBrush", SystemColors.HighlightBrush);
        Set(resources, "DockRunningDotBrush", SystemColors.WindowTextBrush);
        Set(resources, "WidgetItemSurfaceBrush", SystemColors.ControlBrush);
        Set(resources, "WidgetCaretBrush", SystemColors.WindowTextBrush);
        Set(resources, "WidgetDefaultButtonBrush", SystemColors.ControlBrush);
        Set(resources, "WidgetDefaultButtonBorderBrush", SystemColors.ControlTextBrush);
        Set(resources, "WidgetSubtleControlBrush", SystemColors.ControlBrush);
        Set(resources, "WidgetSubtleControlBorderBrush", SystemColors.ControlTextBrush);
        Set(resources, "WidgetControlBrush", SystemColors.ControlBrush);
        Set(resources, "WidgetControlBorderBrush", SystemColors.ControlTextBrush);
        Set(resources, "WidgetInputTextBrush", SystemColors.WindowTextBrush);
        Set(resources, "WidgetTileHoverBrush", SystemColors.ControlBrush);
        Set(resources, "WidgetTileFocusBrush", SystemColors.HighlightBrush);
        Set(resources, "WidgetTileSelectedBrush", SystemColors.HighlightBrush);
        Set(resources, "WidgetTileBorderThickness", new Thickness(1));
        Set(resources, "WidgetButtonHoverBrush", SystemColors.HighlightBrush);
        Set(resources, "WidgetButtonPressedBrush", SystemColors.HighlightBrush);
        Set(resources, "AppearanceInkBrush", SystemColors.WindowTextBrush);
        Set(resources, "AppearanceMutedInkBrush", SystemColors.WindowTextBrush);
        Set(resources, "AppearanceInputBrush", SystemColors.WindowBrush);
        Set(resources, "AppearanceInputBorderBrush", SystemColors.WindowTextBrush);
        Set(resources, "AppearanceInputTextBrush", SystemColors.WindowTextBrush);
        Set(resources, "AppearanceTrackBrush", SystemColors.ControlTextBrush);
        Set(resources, "AppearanceTrackRemainderBrush", SystemColors.ControlBrush);
        Set(resources, "AppearanceThumbBrush", SystemColors.HighlightBrush);
        Set(resources, "AppearanceThumbBorderBrush", SystemColors.HighlightTextBrush);
        Set(resources, "AppearanceDividerBrush", SystemColors.WindowTextBrush);
        Set(resources, "AppearanceSecondaryButtonTextBrush", SystemColors.ControlTextBrush);
        Set(resources, "AppearanceSecondaryButtonBrush", SystemColors.ControlBrush);
        Set(resources, "AppearanceSecondaryButtonBorderBrush", SystemColors.ControlTextBrush);
    }

    private static void ApplyIcePalette(ResourceDictionary resources)
    {
        Set(resources, "PrimaryTextBrush", Brush("#F7FBFF"));
        Set(resources, "SecondaryTextBrush", Brush("#B8D4E7"));
        Set(resources, "AccentBrush", Brush("#A6B8FF"));
        Set(resources, "IcePageBrush", Brush("#F3F6FA"));
        Set(resources, "IcePanelBrush", Brush("#FFFFFF"));
        Set(resources, "IcePanelStrokeBrush", Brush("#E2E8F0"));
        Set(resources, "IceInkBrush", Brush("#1F2937"));
        Set(resources, "IceMutedInkBrush", Brush("#66768A"));
        Set(resources, "IceActionBrush", Brush("#2478C9"));
        Set(resources, "IceActionTextBrush", Brushes.White);
        Set(resources, "SettingsInputBrush", Brush("#FFFFFF"));
        Set(resources, "SettingsInputBorderBrush", Brush("#D6DEE8"));
        Set(resources, "SettingsButtonBrush", Brush("#FFFFFF"));
        Set(resources, "SettingsButtonBorderBrush", Brush("#D6DEE8"));
        Set(resources, "SettingsNavigationBrush", Brush("#F8FAFD"));
        Set(resources, "SettingsStrokeBrush", Brush("#E2E8F0"));
        Set(resources, "SettingsPreviewBrush", Brush("#E7EEF5"));
        Set(resources, "SettingsCardBrush", Brush("#FFFFFF"));
        Set(resources, "SettingsPanelBorderBrush", Brush("#E2E8F0"));
        Set(resources, "SettingsCardBorderBrush", Brush("#E2E8F0"));
        Set(resources, "SettingsCardHoverBrush", Brush("#F8FAFD"));
        Set(resources, "SettingsCardSelectedBrush", Brush("#E7F1FC"));
        Set(resources, "SettingsCardSelectedTextBrush", Brush("#2478C9"));
        Set(resources, "SettingsCardSelectedBorderBrush", Brush("#2478C9"));
        Set(resources, "SettingsPreviewBorderBrush", Brush("#D6DEE8"));
        Set(resources, "SettingsComponentNavigationBrush", Brush("#F8FAFD"));
        Set(resources, "SettingsComponentBorderBrush", Brush("#E2E8F0"));
        Set(resources, "SettingsBadgeBrush", Brush("#E7F1FC"));
        Set(resources, "SettingsBadgeTextBrush", Brush("#52667B"));
        Set(resources, "SettingsResultBrush", Brush("#FFFFFF"));
        Set(resources, "SettingsResultBorderBrush", Brush("#D6DEE8"));
        Set(resources, "SettingsCoordinateTextBrush", Brush("#66768A"));
        Set(resources, "SettingsSurfaceBrush", Brush("#FFFFFF"));
        Set(resources, "SettingsSurfaceHoverBrush", Brush("#F8FAFD"));
        Set(resources, "SettingsSurfaceBorderBrush", Brush("#E2E8F0"));
        Set(resources, "SettingsControlBrush", Brush("#FFFFFF"));
        Set(resources, "SettingsControlHoverBrush", Brush("#F8FAFD"));
        Set(resources, "SettingsControlBorderBrush", Brush("#D6DEE8"));
        Set(resources, "SettingsToggleOffBrush", Brush("#64748B"));
        Set(resources, "SettingsTrackBrush", Brush("#DCE4ED"));
        Set(resources, "SettingsSegmentTrackBrush", Brush("#EEF2F7"));
        Set(resources, "SettingsNavSelectedBrush", Brush("#E7F1FC"));
        Set(resources, "SettingsNavSelectedTextBrush", Brush("#2478C9"));
        Set(resources, "SettingsChoiceSelectedBrush", Brush("#FFFFFF"));
        Set(resources, "SettingsIconBlueBrush", Brush("#2F7CF6"));
        Set(resources, "SettingsIconIndigoBrush", Brush("#5B5FE8"));
        Set(resources, "SettingsIconPurpleBrush", Brush("#9A5BE0"));
        Set(resources, "SettingsIconPinkBrush", Brush("#E5517A"));
        Set(resources, "SettingsIconOrangeBrush", Brush("#F08A24"));
        Set(resources, "SettingsIconGreenBrush", Brush("#2EA85A"));
        Set(resources, "SettingsIconTealBrush", Brush("#16A0B0"));
        Set(resources, "SettingsIconGrayBrush", Brush("#7F8C99"));
        Set(resources, "SettingsIconGlyphBrush", Brush("#FFFFFF"));
        Set(resources, "DockSurfaceBrush", Brush("#D6EAF5FC"));
        Set(resources, "DockHandleBrush", Brush("#F2AACEF8"));
        Set(resources, "DockSurfaceBorderBrush", Brush("#BFFFFFFF"));
        Set(resources, "DockActiveDotBrush", Brush("#E0559EF3"));
        Set(resources, "PerformanceAccentBrush", Brush("#F51EA9FF"));
        Set(resources, "TodoDueSoonBrush", Brush("#261EA9FF"));
        Set(resources, "DockRunningDotBrush", Brush("#C21F3A55"));
        Set(resources, "WidgetItemSurfaceBrush", Brush("#7AFFFFFF"));
        Set(resources, "WidgetCaretBrush", Brushes.White);
        Set(resources, "WidgetDefaultButtonBrush", Brush("#1FFFFFFF"));
        Set(resources, "WidgetDefaultButtonBorderBrush", Brush("#28FFFFFF"));
        Set(resources, "WidgetSubtleControlBrush", Brush("#35FFFFFF"));
        Set(resources, "WidgetSubtleControlBorderBrush", Brush("#52FFFFFF"));
        Set(resources, "WidgetControlBrush", Brush("#66FFFFFF"));
        Set(resources, "WidgetControlBorderBrush", Brush("#66FFFFFF"));
        Set(resources, "WidgetInputTextBrush", Brush("#243146"));
        Set(resources, "WidgetTileHoverBrush", Brush("#24FFFFFF"));
        Set(resources, "WidgetTileFocusBrush", Brush("#3DFFFFFF"));
        Set(resources, "WidgetTileSelectedBrush", Brush("#4DFFFFFF"));
        Set(resources, "WidgetTileBorderThickness", new Thickness(0));
        Set(resources, "WidgetButtonHoverBrush", Brush("#32FFFFFF"));
        Set(resources, "WidgetButtonPressedBrush", Brush("#48FFFFFF"));
        Set(resources, "AppearanceInkBrush", Brush("#203047"));
        Set(resources, "AppearanceMutedInkBrush", Brush("#607386"));
        Set(resources, "AppearanceInputBrush", Brush("#58FFFFFF"));
        Set(resources, "AppearanceInputBorderBrush", Brush("#4B74869A"));
        Set(resources, "AppearanceInputTextBrush", Brush("#243146"));
        Set(resources, "AppearanceTrackBrush", Brush("#7A34465C"));
        Set(resources, "AppearanceTrackRemainderBrush", Brush("#2434465C"));
        Set(resources, "AppearanceThumbBrush", Brushes.White);
        Set(resources, "AppearanceThumbBorderBrush", Brush("#72869AAB"));
        Set(resources, "AppearanceDividerBrush", Brush("#36748698"));
        Set(resources, "AppearanceSecondaryButtonTextBrush", Brush("#34465C"));
        Set(resources, "AppearanceSecondaryButtonBrush", Brush("#4CFFFFFF"));
        Set(resources, "AppearanceSecondaryButtonBorderBrush", Brush("#55FFFFFF"));
    }

    private static SolidColorBrush Brush(string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value)!;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void Set(ResourceDictionary resources, string key, object value) =>
        resources[key] = value;
}
