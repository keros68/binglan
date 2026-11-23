namespace BingLan.Core.Models;

/// <summary>
/// The four fixed desktop surfaces managed in the "各部分外观" section of AppearancePage.
/// </summary>
public enum AppearancePartKind
{
    DesktopCards,
    Launcher,
    Dock,
    TopBar
}

/// <summary>
/// Metadata for one fixed row in the "各部分外观" section.
/// </summary>
public sealed record AppearancePartDescriptor(
    AppearancePartKind Kind,
    string Key,
    string Title);

/// <summary>
/// Computed display status and restore availability for one "各部分外观" row.
/// </summary>
public readonly record struct AppearancePartStatus(
    AppearancePartKind Kind,
    bool IsCustomized,
    string StatusText,
    bool CanRestoreFollow);

/// <summary>
/// Pure rules for the centralized "各部分外观" section on AppearancePage:
/// fixed row order, status text, accordion mutual exclusion, cross-page summary text,
/// and control-to-part resolution for settings search and jump navigation.
/// </summary>
public static class AppearancePartLookRules
{
    public const string SectionTitle = "各部分外观";

    public const string CardsPartKey = "Cards";
    public const string LauncherPartKey = "Launcher";
    public const string DockPartKey = "Dock";
    public const string TopBarPartKey = "TopBar";

    public const string FollowGlobalText = "跟随全局";
    public const string CustomizedText = "已自定义";
    public const string AllCardsFollowGlobalText = "全部跟随全局";
    public const string CustomizeButtonText = "自定义…";
    public const string CollapseButtonText = "收起";

    public static IReadOnlyList<AppearancePartDescriptor> Parts { get; } =
    [
        new(AppearancePartKind.DesktopCards, CardsPartKey, "桌面卡片"),
        new(AppearancePartKind.Launcher, LauncherPartKey, "冰蓝总览卡"),
        new(AppearancePartKind.Dock, DockPartKey, "冰蓝 Dock"),
        new(AppearancePartKind.TopBar, TopBarPartKey, "顶部信息条")
    ];

    private static readonly Dictionary<string, AppearancePartKind> ControlToPart = new(StringComparer.Ordinal)
    {
        // Desktop cards summary row inside AppearancePage
        ["AppearanceCardsPartRow"] = AppearancePartKind.DesktopCards,
        ["AppearanceCardsPartEditor"] = AppearancePartKind.DesktopCards,
        ["AppearanceCardsSummaryRow"] = AppearancePartKind.DesktopCards,
        ["AppearanceViewCustomCardsButton"] = AppearancePartKind.DesktopCards,

        // Launcher look controls moved from LauncherPage
        ["AppearanceLauncherPartRow"] = AppearancePartKind.Launcher,
        ["AppearanceLauncherPartEditor"] = AppearancePartKind.Launcher,
        ["LauncherLookSettingsRow"] = AppearancePartKind.Launcher,
        ["LauncherFollowLookRadio"] = AppearancePartKind.Launcher,
        ["LauncherCustomLookRadio"] = AppearancePartKind.Launcher,
        ["LauncherCustomLookPanel"] = AppearancePartKind.Launcher,
        ["LauncherPaletteSwatches"] = AppearancePartKind.Launcher,
        ["LauncherColorPreview"] = AppearancePartKind.Launcher,
        ["LauncherColorEditor"] = AppearancePartKind.Launcher,
        ["LauncherOpacitySlider"] = AppearancePartKind.Launcher,
        ["LauncherCornerRadiusSlider"] = AppearancePartKind.Launcher,

        // Dock look controls moved from DockPage
        ["AppearanceDockPartRow"] = AppearancePartKind.Dock,
        ["AppearanceDockPartEditor"] = AppearancePartKind.Dock,
        ["DockFrostedRow"] = AppearancePartKind.Dock,
        ["DockFrostedRadio"] = AppearancePartKind.Dock,
        ["DockClearGlassRadio"] = AppearancePartKind.Dock,
        ["DockLookSettingsRow"] = AppearancePartKind.Dock,
        ["DockFollowLookRadio"] = AppearancePartKind.Dock,
        ["DockCustomLookRadio"] = AppearancePartKind.Dock,
        ["DockCustomLookPanel"] = AppearancePartKind.Dock,
        ["DockPaletteSwatches"] = AppearancePartKind.Dock,
        ["DockColorPreview"] = AppearancePartKind.Dock,
        ["DockColorEditor"] = AppearancePartKind.Dock,
        ["DockOpacitySlider"] = AppearancePartKind.Dock,

        // TopBar look controls moved from TopBarPage
        ["AppearanceTopBarPartRow"] = AppearancePartKind.TopBar,
        ["AppearanceTopBarPartEditor"] = AppearancePartKind.TopBar,
        ["TopBarLookPanel"] = AppearancePartKind.TopBar,
        ["TopBarFrostedRow"] = AppearancePartKind.TopBar,
        ["TopBarFrostedCheckBox"] = AppearancePartKind.TopBar,
        ["TopBarLookSettingsRow"] = AppearancePartKind.TopBar,
        ["TopBarFollowLookRadio"] = AppearancePartKind.TopBar,
        ["TopBarCustomLookRadio"] = AppearancePartKind.TopBar,
        ["TopBarCustomLookPanel"] = AppearancePartKind.TopBar,
        ["TopBarColorPreview"] = AppearancePartKind.TopBar,
        ["TopBarColorEditor"] = AppearancePartKind.TopBar,
        ["TopBarOpacitySlider"] = AppearancePartKind.TopBar
    };

    public static IReadOnlyDictionary<string, AppearancePartKind> RegisteredControlMappings => ControlToPart;

    public static string GetPartKey(AppearancePartKind part) => part switch
    {
        AppearancePartKind.DesktopCards => CardsPartKey,
        AppearancePartKind.Launcher => LauncherPartKey,
        AppearancePartKind.Dock => DockPartKey,
        AppearancePartKind.TopBar => TopBarPartKey,
        _ => CardsPartKey
    };

    public static string GetPartTitle(AppearancePartKind part) => part switch
    {
        AppearancePartKind.DesktopCards => "桌面卡片",
        AppearancePartKind.Launcher => "冰蓝总览卡",
        AppearancePartKind.Dock => "冰蓝 Dock",
        AppearancePartKind.TopBar => "顶部信息条",
        _ => "桌面卡片"
    };

    public static bool TryParsePartKey(string? key, out AppearancePartKind part)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            part = default;
            return false;
        }

        switch (key.Trim())
        {
            case var s when string.Equals(s, CardsPartKey, StringComparison.OrdinalIgnoreCase):
                part = AppearancePartKind.DesktopCards;
                return true;
            case var s when string.Equals(s, LauncherPartKey, StringComparison.OrdinalIgnoreCase):
                part = AppearancePartKind.Launcher;
                return true;
            case var s when string.Equals(s, DockPartKey, StringComparison.OrdinalIgnoreCase):
                part = AppearancePartKind.Dock;
                return true;
            case var s when string.Equals(s, TopBarPartKey, StringComparison.OrdinalIgnoreCase):
                part = AppearancePartKind.TopBar;
                return true;
            default:
                part = default;
                return false;
        }
    }

    /// <summary>
    /// Evaluates the status of a binary-follow surface (Launcher, Dock, or TopBar).
    /// </summary>
    public static AppearancePartStatus EvaluateBinaryPart(AppearancePartKind kind, bool followGlobal) =>
        new(
            kind,
            IsCustomized: !followGlobal,
            StatusText: followGlobal ? FollowGlobalText : CustomizedText,
            CanRestoreFollow: !followGlobal);

    /// <summary>
    /// Evaluates the status of the Desktop Cards row based on how many cards use an independent look.
    /// </summary>
    public static AppearancePartStatus EvaluateDesktopCardsPart(int customCardCount)
    {
        var count = Math.Max(0, customCardCount);
        var hasCustom = count > 0;
        return new AppearancePartStatus(
            AppearancePartKind.DesktopCards,
            IsCustomized: hasCustom,
            StatusText: hasCustom ? $"{count} 张卡片使用独立外观" : AllCardsFollowGlobalText,
            CanRestoreFollow: hasCustom);
    }

    /// <summary>
    /// One-line summary header shown on LauncherPage, DockPage, and TopBarPage.
    /// </summary>
    public static string FormatPageSummary(bool followGlobal) =>
        followGlobal ? $"外观：{FollowGlobalText}" : $"外观：{CustomizedText}";

    /// <summary>
    /// Button label for the in-place accordion toggle action on each row.
    /// </summary>
    public static string FormatCustomizeButtonLabel(bool isExpanded) =>
        isExpanded ? CollapseButtonText : CustomizeButtonText;

    /// <summary>
    /// Action button label inside the expanded Desktop Cards summary row.
    /// </summary>
    public static string FormatViewCardsButtonLabel(int customCardCount) =>
        customCardCount > 0 ? "查看使用独立外观的卡片 ›" : "前往桌面卡片页 ›";

    /// <summary>
    /// Description text inside the expanded Desktop Cards summary row.
    /// </summary>
    public static string FormatCardsExpandedSummary(string sharedLookSummary, int customCardCount)
    {
        var count = Math.Max(0, customCardCount);
        var summary = string.IsNullOrWhiteSpace(sharedLookSummary) ? "默认外观" : sharedLookSummary.Trim();
        return count > 0
            ? $"当前全局外观：{summary}；有 {count} 张卡片使用独立外观，可前往桌面卡片页逐张调整。"
            : $"所有桌面卡片均跟随全局外观（{summary}），如需给单张卡片单独设色，可前往桌面卡片页调整。";
    }

    /// <summary>
    /// Pure accordion toggle: clicking the currently expanded part collapses it;
    /// clicking another part expands it and collapses the previous one.
    /// </summary>
    public static AppearancePartKind? ToggleAccordion(
        AppearancePartKind? currentExpanded,
        AppearancePartKind clicked) =>
        currentExpanded == clicked ? null : clicked;

    /// <summary>
    /// Pure accordion expand for jump/search targets: always opens <paramref name="target"/>
    /// and collapses any other open part.
    /// </summary>
    public static AppearancePartKind ExpandAccordion(
        AppearancePartKind? currentExpanded,
        AppearancePartKind target)
    {
        _ = currentExpanded;
        return target;
    }

    /// <summary>
    /// Resolves which "各部分外观" accordion part owns a control name, if any.
    /// </summary>
    public static AppearancePartKind? ResolvePartForControl(string? controlName)
    {
        if (string.IsNullOrWhiteSpace(controlName))
        {
            return null;
        }

        return ControlToPart.TryGetValue(controlName, out var part) ? part : null;
    }
}
