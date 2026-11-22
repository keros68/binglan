namespace BingLan.Core.Models;

/// <summary>
/// Describes one "I want to..." quick action shortcut on the settings home page.
/// </summary>
public sealed record SettingsQuickAction(
    string Key,
    string Label,
    string Glyph,
    string AutomationName,
    string TargetKey,
    string ExpectedPageKey,
    string? ExpectedScope = null,
    string? FallbackTargetKey = null,
    string? FallbackStatusMessage = null);

public enum SettingsQuickActionExecutionMode
{
    Unavailable,
    OpenPrimarySearchEntry,
    JumpToPrimaryTarget,
    JumpToFallbackTarget
}

public readonly record struct SettingsQuickActionResolution(
    SettingsQuickActionExecutionMode Mode,
    bool ShowFallbackStatus);

public enum SettingsRevealAction
{
    Abort,
    Complete,
    Retry,
    GiveUp
}

/// <summary>
/// Pure definition of the settings home page "I want to..." task shortcuts and their target controls.
/// </summary>
public static class SettingsQuickActions
{
    public const string SectionTitle = "我想…";
    public const string UnavailableStatusMessage = "暂时无法跳转到这项设置";
    public const int MaxRevealRetries = 2;

    public const string ChangeAppearanceKey = "ChangeAppearance";
    public const string ToggleBarsKey = "ToggleBars";
    public const string SetWeatherCityKey = "SetWeatherCity";
    public const string EditHotkeysKey = "EditHotkeys";
    public const string HideDesktopIconsKey = "HideDesktopIcons";
    public const string OrganizeDesktopFilesKey = "OrganizeDesktopFiles";

    public static IReadOnlyList<SettingsQuickAction> Items { get; } =
    [
        new(
            ChangeAppearanceKey,
            "换个外观风格",
            "\uE790",
            "我想：换个外观风格",
            TargetKey: "AppearanceGlassRow",
            ExpectedPageKey: SettingsNavigationRules.AppearancePageKey),
        new(
            ToggleBarsKey,
            "开关 Dock 或顶栏",
            "\uE7C4",
            "我想：开关 Dock 或顶栏",
            TargetKey: "DockEnabledRow",
            ExpectedPageKey: SettingsNavigationRules.DockPageKey),
        new(
            SetWeatherCityKey,
            "设置天气城市",
            "\uE753",
            "我想：设置天气城市",
            TargetKey: "WeatherCityPanel",
            ExpectedPageKey: SettingsNavigationRules.ComponentsPageKey,
            ExpectedScope: "Kind:Weather",
            FallbackTargetKey: "DesktopComponentList"),
        new(
            EditHotkeysKey,
            "修改快捷键",
            "\uE765",
            "我想：修改快捷键",
            TargetKey: "HotkeysSettingsGroup",
            ExpectedPageKey: SettingsNavigationRules.GeneralPageKey),
        new(
            HideDesktopIconsKey,
            "隐藏桌面图标",
            "\uE7F4",
            "我想：隐藏桌面图标",
            TargetKey: "CleanDesktopRow",
            ExpectedPageKey: SettingsNavigationRules.SchemePageKey),
        new(
            OrganizeDesktopFilesKey,
            "整理桌面文件",
            "\uE8B7",
            "我想：整理桌面文件",
            TargetKey: "FileBoxViewModeRow",
            ExpectedPageKey: SettingsNavigationRules.ComponentsPageKey,
            ExpectedScope: "FileBox",
            FallbackTargetKey: "AddFileBoxButton",
            FallbackStatusMessage: "当前还没有文件收纳盒，点击上方“+ 文件收纳盒”即可新建")
    ];

    public static SettingsQuickAction? FindByKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var item in Items)
        {
            if (string.Equals(item.Key, key, StringComparison.Ordinal))
            {
                return item;
            }
        }

        return null;
    }

    /// <summary>
    /// Decides how a quick action should execute without discarding a resolved primary target
    /// or showing an empty-domain fallback prompt when the primary domain actually exists.
    /// </summary>
    public static SettingsQuickActionResolution ResolveExecution(
        SettingsQuickAction action,
        bool hasPrimaryTarget,
        bool hasPrimarySearchEntry,
        bool isPrimarySearchEntryAvailable,
        bool hasFallbackTarget,
        bool isPrimaryDomainAvailable = true)
    {
        ArgumentNullException.ThrowIfNull(action);

        var hasFallbackKey = !string.IsNullOrWhiteSpace(action.FallbackTargetKey);
        var canUseFallback = hasFallbackKey && hasFallbackTarget;
        var requiresEmptyDomainForFallback = !string.IsNullOrWhiteSpace(action.FallbackStatusMessage);

        if (!isPrimaryDomainAvailable)
        {
            return canUseFallback
                ? new SettingsQuickActionResolution(
                    SettingsQuickActionExecutionMode.JumpToFallbackTarget,
                    ShowFallbackStatus: requiresEmptyDomainForFallback)
                : new SettingsQuickActionResolution(
                    SettingsQuickActionExecutionMode.Unavailable,
                    ShowFallbackStatus: false);
        }

        if (hasPrimaryTarget)
        {
            if (hasPrimarySearchEntry && isPrimarySearchEntryAvailable)
            {
                return new SettingsQuickActionResolution(
                    SettingsQuickActionExecutionMode.OpenPrimarySearchEntry,
                    ShowFallbackStatus: false);
            }

            return new SettingsQuickActionResolution(
                SettingsQuickActionExecutionMode.JumpToPrimaryTarget,
                ShowFallbackStatus: false);
        }

        if (canUseFallback && !requiresEmptyDomainForFallback)
        {
            return new SettingsQuickActionResolution(
                SettingsQuickActionExecutionMode.JumpToFallbackTarget,
                ShowFallbackStatus: false);
        }

        return new SettingsQuickActionResolution(
            SettingsQuickActionExecutionMode.Unavailable,
            ShowFallbackStatus: false);
    }

    public static long NextRevealGeneration(long currentGeneration) =>
        currentGeneration < 0 ? 1 : currentGeneration + 1;

    public static bool IsCurrentRevealGeneration(long requestGeneration, long latestGeneration) =>
        requestGeneration > 0 && requestGeneration == latestGeneration;

    public static SettingsRevealAction EvaluateRevealAttempt(
        long requestGeneration,
        long latestGeneration,
        bool isWindowClosed,
        bool isTargetVisible,
        int remainingRetries)
    {
        if (isWindowClosed || !IsCurrentRevealGeneration(requestGeneration, latestGeneration))
        {
            return SettingsRevealAction.Abort;
        }

        if (isTargetVisible)
        {
            return SettingsRevealAction.Complete;
        }

        return remainingRetries > 0
            ? SettingsRevealAction.Retry
            : SettingsRevealAction.GiveUp;
    }
}
