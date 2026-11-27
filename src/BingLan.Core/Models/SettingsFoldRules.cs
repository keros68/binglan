namespace BingLan.Core.Models;

/// <summary>
/// Describes one collapsible fold in the settings window.
/// </summary>
public sealed record SettingsFoldDescriptor(
    string FoldKey,
    string PageKey,
    string? SubScope,
    string Title,
    bool IsPreExisting);

/// <summary>
/// Summarizes the always-visible setting count and collapsible folds for one settings page or sub-view.
/// </summary>
public sealed record SettingsPageExposureSummary(
    string PageKey,
    string ScopeKey,
    int VisibleSettingCount,
    IReadOnlyList<string> FoldKeys);

/// <summary>
/// Pure rules describing which settings live inside collapsible "More options" or existing folds,
/// and which folds must be expanded when a deep link or settings search targets a setting.
/// </summary>
public static class SettingsFoldRules
{
    public const string MoreOptionsTitle = "更多选项";

    public const string AppearanceMoreFold = "AppearanceMoreFold";
    public const string AppearanceLayoutFold = "AppearanceLayoutFold";
    public const string ComponentMoreFold = "ComponentMoreFold";
    public const string ComponentOwnLookFold = "ComponentOwnLookFold";
    public const string SelectedWidgetMoreFold = "SelectedWidgetMoreFold";
    public const string SelectedWidgetOwnLookFold = "SelectedWidgetOwnLookFold";
    public const string LauncherMoreFold = "LauncherMoreFold";
    public const string DockAdvancedFold = "DockAdvancedFold";
    public const string TopBarOrderFold = "TopBarOrderFold";
    public const string TopBarAdvancedFold = "TopBarAdvancedFold";
    public const string GeneralMoreFold = "GeneralMoreFold";

    public static IReadOnlyList<SettingsFoldDescriptor> Folds { get; } =
    [
        new(AppearanceMoreFold, SettingsNavigationRules.AppearancePageKey, null, MoreOptionsTitle, IsPreExisting: false),
        new(AppearanceLayoutFold, SettingsNavigationRules.AppearancePageKey, null, "布局预设与主题", IsPreExisting: true),
        new(ComponentMoreFold, SettingsNavigationRules.ComponentsPageKey, "Builtin", MoreOptionsTitle, IsPreExisting: false),
        new(ComponentOwnLookFold, SettingsNavigationRules.ComponentsPageKey, "Builtin", "独立外观", IsPreExisting: true),
        new(SelectedWidgetMoreFold, SettingsNavigationRules.ComponentsPageKey, "Widget", MoreOptionsTitle, IsPreExisting: false),
        new(SelectedWidgetOwnLookFold, SettingsNavigationRules.ComponentsPageKey, "Widget", "独立外观", IsPreExisting: true),
        new(DockAdvancedFold, SettingsNavigationRules.DockPageKey, null, "尺寸与高级", IsPreExisting: true),
        new(TopBarOrderFold, SettingsNavigationRules.TopBarPageKey, null, "模块顺序", IsPreExisting: true),
        new(TopBarAdvancedFold, SettingsNavigationRules.TopBarPageKey, null, "高度", IsPreExisting: true),
        new(LauncherMoreFold, SettingsNavigationRules.LauncherPageKey, null, MoreOptionsTitle, IsPreExisting: false),
        new(GeneralMoreFold, SettingsNavigationRules.GeneralPageKey, null, MoreOptionsTitle, IsPreExisting: false)
    ];

    public static IReadOnlyList<SettingsPageExposureSummary> PageExposures { get; } =
    [
        new(SettingsNavigationRules.SchemePageKey, SettingsNavigationRules.SchemePageKey, 5, []),
        new(SettingsNavigationRules.ComponentsPageKey, "Builtin", 8, [ComponentMoreFold, ComponentOwnLookFold]),
        new(SettingsNavigationRules.ComponentsPageKey, "Widget", 8, [SelectedWidgetMoreFold, SelectedWidgetOwnLookFold]),
        new(SettingsNavigationRules.AppearancePageKey, SettingsNavigationRules.AppearancePageKey, 6, [AppearanceMoreFold, AppearanceLayoutFold]),
        new(SettingsNavigationRules.DockPageKey, SettingsNavigationRules.DockPageKey, 8, [DockAdvancedFold]),
        new(SettingsNavigationRules.TopBarPageKey, SettingsNavigationRules.TopBarPageKey, 6, [TopBarOrderFold, TopBarAdvancedFold]),
        new(SettingsNavigationRules.TaskbarPageKey, SettingsNavigationRules.TaskbarPageKey, 3, []),
        new(SettingsNavigationRules.LauncherPageKey, SettingsNavigationRules.LauncherPageKey, 6, [LauncherMoreFold]),
        new(SettingsNavigationRules.GeneralPageKey, SettingsNavigationRules.GeneralPageKey, 7, [GeneralMoreFold])
    ];

    private static readonly Dictionary<string, string> ControlToFold = new(StringComparer.Ordinal)
    {
        // AppearancePage -> AppearanceMoreFold
        ["RecommendedClearLookRow"] = AppearanceMoreFold,
        ["RecommendedClearLookButton"] = AppearanceMoreFold,
        ["ItemSurfacePanel"] = AppearanceMoreFold,
        ["ItemSurfaceLightRadio"] = AppearanceMoreFold,
        ["ItemSurfaceDarkRadio"] = AppearanceMoreFold,
        ["ItemSurfaceStrengthSlider"] = AppearanceMoreFold,
        ["CardShadowSelector"] = AppearanceMoreFold,
        ["CardSpacingSelector"] = AppearanceMoreFold,
        ["GridSnapCheckBox"] = AppearanceMoreFold,
        ["HeaderToolsOnHoverCheckBox"] = AppearanceMoreFold,

        // AppearancePage -> AppearanceLayoutFold
        ["ApplyLayoutButton"] = AppearanceLayoutFold,
        ["PresetPreviewImage"] = AppearanceLayoutFold,
        ["QuietInformationPresetRadio"] = AppearanceLayoutFold,
        ["GlacierWorkbenchPresetRadio"] = AppearanceLayoutFold,
        ["TransparentApplePresetRadio"] = AppearanceLayoutFold,
        ["CenterClockPresetRadio"] = AppearanceLayoutFold,
        ["RainmeterImportStatusText"] = AppearanceLayoutFold,

        // ComponentsPage (BuiltIn) -> ComponentMoreFold
        ["ClockDividerRow"] = ComponentMoreFold,
        ["ClockDividerCheckBox"] = ComponentMoreFold,
        ["ClockDividerThicknessSlider"] = ComponentMoreFold,

        // ComponentsPage (BuiltIn) -> ComponentOwnLookFold
        ["ComponentFollowSharedLookRow"] = ComponentOwnLookFold,
        ["ComponentFollowSharedLookButton"] = ComponentOwnLookFold,
        ["ComponentLookLightRadio"] = ComponentOwnLookFold,
        ["ComponentLookDarkRadio"] = ComponentOwnLookFold,
        ["ComponentLookClearRadio"] = ComponentOwnLookFold,
        ["SelectedComponentFontRow"] = ComponentOwnLookFold,
        ["SelectedComponentFontPicker"] = ComponentOwnLookFold,
        ["SelectedComponentFontScaleSlider"] = ComponentOwnLookFold,
        ["SelectedComponentTextColorEditor"] = ComponentOwnLookFold,
        ["SelectedComponentBackgroundColorEditor"] = ComponentOwnLookFold,
        ["SelectedComponentHeaderRows"] = ComponentOwnLookFold,
        ["SelectedComponentHeaderColorEditor"] = ComponentOwnLookFold,
        ["SelectedComponentHeaderOpacitySlider"] = ComponentOwnLookFold,
        ["SelectedComponentOpacitySlider"] = ComponentOwnLookFold,
        ["SelectedComponentCornerRadiusSlider"] = ComponentOwnLookFold,
        ["SelectedComponentDensityRow"] = ComponentOwnLookFold,
        ["SelectedComponentDensitySelector"] = ComponentOwnLookFold,
        ["SelectedComponentWidthEditor"] = ComponentOwnLookFold,
        ["SelectedComponentHeightEditor"] = ComponentOwnLookFold,

        // ComponentsPage (Widget/FileBox) -> SelectedWidgetMoreFold
        ["FileBoxCollectPanel"] = SelectedWidgetMoreFold,
        ["FileBoxCollectTypes"] = SelectedWidgetMoreFold,
        ["FileBoxExtraExtensionsEditor"] = SelectedWidgetMoreFold,
        ["FileBoxDesktopHintPanel"] = SelectedWidgetMoreFold,
        ["FileBoxAutomationCheckBox"] = SelectedWidgetMoreFold,
        ["FileBoxDeleteOriginalsCheckBox"] = SelectedWidgetMoreFold,

        // ComponentsPage (Widget/FileBox) -> SelectedWidgetOwnLookFold
        ["SelectedWidgetFollowSharedLookRow"] = SelectedWidgetOwnLookFold,
        ["SelectedWidgetFollowSharedLookButton"] = SelectedWidgetOwnLookFold,
        ["SelectedWidgetAppearanceHost"] = SelectedWidgetOwnLookFold,

        // LauncherPage -> LauncherMoreFold
        ["LauncherFavoriteModeRow"] = LauncherMoreFold,
        ["LauncherFavoriteModeComboBox"] = LauncherMoreFold,

        // DockPage -> DockAdvancedFold
        ["DockStartMenuButtonCheckBox"] = DockAdvancedFold,
        ["DockLauncherButtonCheckBox"] = DockAdvancedFold,
        ["DockShowDesktopButtonCheckBox"] = DockAdvancedFold,
        ["DockTaskViewButtonCheckBox"] = DockAdvancedFold,
        ["DockAutoIconSizeCheckBox"] = DockAdvancedFold,
        ["DockHoverMagnificationScaleRow"] = DockAdvancedFold,
        ["DockHoverMagnificationScaleSlider"] = DockAdvancedFold,
        ["DockBottomGapSlider"] = DockAdvancedFold,
        ["MotionSelector"] = DockAdvancedFold,
        ["DockHiddenList"] = DockAdvancedFold,

        // TopBarPage -> TopBarOrderFold / TopBarAdvancedFold
        ["TopBarOrderPanel"] = TopBarOrderFold,
        ["TopBarResetOrderButton"] = TopBarOrderFold,
        ["TopBarHeightSlider"] = TopBarAdvancedFold,

        // GeneralPage -> GeneralMoreFold
        ["ToggleWidgetsHotkeyButton"] = GeneralMoreFold,
        ["QuickTodoHotkeyButton"] = GeneralMoreFold,
        ["ToggleDesktopIconsHotkeyButton"] = GeneralMoreFold,
        ["BackupList"] = GeneralMoreFold,
        ["DataDirectoryText"] = GeneralMoreFold,
        ["UninstallRow"] = GeneralMoreFold,
        ["UninstallButton"] = GeneralMoreFold,
        ["UninstallStatusText"] = GeneralMoreFold
    };

    private static readonly HashSet<string> KnownFoldKeys = new(
        Folds.Select(fold => fold.FoldKey),
        StringComparer.Ordinal);

    /// <summary>
    /// Returns the fold that must be expanded when navigating to a legacy or deep-link section.
    /// </summary>
    public static string? ResolveFoldForSection(string? section) => section switch
    {
        "Layout" or "Theme" => AppearanceLayoutFold,
        "Backup" or "Privacy" => GeneralMoreFold,
        _ => null
    };

    /// <summary>
    /// Looks up the registered fold key for a setting control name, if it lives inside a fold.
    /// </summary>
    public static bool TryGetFoldForSetting(string? controlName, out string foldKey)
    {
        if (!string.IsNullOrWhiteSpace(controlName) && ControlToFold.TryGetValue(controlName, out var found))
        {
            foldKey = found;
            return true;
        }

        foldKey = string.Empty;
        return false;
    }

    /// <summary>
    /// Resolves all folds that must be expanded to reveal a target setting, preserving outer-to-inner order
    /// and removing duplicates.
    /// </summary>
    public static IReadOnlyList<string> ResolveFoldsToExpand(
        string? targetControlName,
        IEnumerable<string>? ancestorFoldKeys,
        bool targetIsFold = false)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
            {
                result.Add(key);
            }
        }

        if (ancestorFoldKeys is not null)
        {
            foreach (var ancestor in ancestorFoldKeys)
            {
                Add(ancestor);
            }
        }

        if (TryGetFoldForSetting(targetControlName, out var mapped))
        {
            Add(mapped);
        }

        if (targetIsFold || (!string.IsNullOrWhiteSpace(targetControlName) && KnownFoldKeys.Contains(targetControlName)))
        {
            Add(targetControlName);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> RegisteredControlMappings => ControlToFold;
}
