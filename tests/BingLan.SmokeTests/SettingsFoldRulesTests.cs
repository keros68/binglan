using BingLan.Core.Models;

namespace BingLan.SmokeTests;

internal static class SettingsFoldRulesTests
{
    public static void Run()
    {
        Check(SettingsFoldRules.Folds.Count == 10, "应登记全部 10 个设置页折叠区（LauncherLookSection 已并入外观页各部分外观）");
        Check(SettingsFoldRules.Folds.Select(fold => fold.FoldKey).Distinct(StringComparer.Ordinal).Count()
                == SettingsFoldRules.Folds.Count,
            "折叠区 Key 不得重复");

        var dockFold = SettingsFoldRules.Folds.Single(fold => fold.FoldKey == SettingsFoldRules.DockAdvancedFold);
        var topBarFold = SettingsFoldRules.Folds.Single(fold => fold.FoldKey == SettingsFoldRules.TopBarAdvancedFold);
        Check(dockFold.Title == "尺寸与高级" && !dockFold.Title.Contains("外观", StringComparison.Ordinal),
            "Dock 高级折叠区标题不应再含“外观”");
        Check(topBarFold.Title == "高度" && !topBarFold.Title.Contains("外观", StringComparison.Ordinal),
            "顶部信息条高级折叠区标题不应再含“外观”");

        foreach (var fold in SettingsFoldRules.Folds.Where(fold => !fold.IsPreExisting))
        {
            Check(fold.Title == SettingsFoldRules.MoreOptionsTitle,
                $"新增折叠区 {fold.FoldKey} 标题应统一为“{SettingsFoldRules.MoreOptionsTitle}”");
        }

        foreach (var exposure in SettingsFoldRules.PageExposures)
        {
            Check(SettingsNavigationRules.IsKnownPageKey(exposure.PageKey),
                $"PageExposures 引用了无效页面键：{exposure.PageKey}");
            var isMinimalPage = exposure.ScopeKey is SettingsNavigationRules.SchemePageKey or SettingsNavigationRules.TaskbarPageKey;
            Check(isMinimalPage
                    ? exposure.VisibleSettingCount is >= 1 and <= 8
                    : exposure.VisibleSettingCount is >= 5 and <= 8,
                $"页面 {exposure.PageKey}/{exposure.ScopeKey} 常驻设置数 {exposure.VisibleSettingCount} 应保持精简");
        }

        // Folds 的 PageKey/SubScope 与 PageExposures 的 PageKey/ScopeKey 命名必须完全一致且一一对应。
        var foldsByKey = SettingsFoldRules.Folds.ToDictionary(fold => fold.FoldKey, StringComparer.Ordinal);
        var referencedFolds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exposure in SettingsFoldRules.PageExposures)
        {
            foreach (var foldKey in exposure.FoldKeys)
            {
                Check(foldsByKey.TryGetValue(foldKey, out var fold),
                    $"PageExposures 引用了未登记的折叠区 {foldKey}");
                Check(referencedFolds.Add(foldKey),
                    $"折叠区 {foldKey} 不应被多个 PageExposures 重复引用");
                Check(fold!.PageKey == exposure.PageKey,
                    $"折叠区 {foldKey} 的 PageKey ({fold.PageKey}) 与 PageExposures ({exposure.PageKey}) 不一致");
                var expectedScope = fold.SubScope ?? fold.PageKey;
                Check(expectedScope == exposure.ScopeKey,
                    $"折叠区 {foldKey} 的范围 ({expectedScope}) 与 PageExposures.ScopeKey ({exposure.ScopeKey}) 不一致");
            }
        }
        Check(referencedFolds.Count == SettingsFoldRules.Folds.Count,
            "所有已登记的折叠区都必须挂接到 PageExposures");

        Check(SettingsFoldRules.ResolveFoldForSection("Layout") == SettingsFoldRules.AppearanceLayoutFold,
            "Layout 深链应展开布局预设与主题折叠区");
        Check(SettingsFoldRules.ResolveFoldForSection("Theme") == SettingsFoldRules.AppearanceLayoutFold,
            "Theme 深链应展开布局预设与主题折叠区");
        Check(SettingsFoldRules.ResolveFoldForSection("Backup") == SettingsFoldRules.GeneralMoreFold,
            "Backup 深链应展开通用设置更多选项");
        Check(SettingsFoldRules.ResolveFoldForSection("Privacy") == SettingsFoldRules.GeneralMoreFold,
            "Privacy 深链应展开通用设置更多选项");
        Check(SettingsFoldRules.ResolveFoldForSection("CleanDesktop") is null
                && SettingsFoldRules.ResolveFoldForSection("Dock") is null
                && SettingsFoldRules.ResolveFoldForSection(null) is null,
            "常驻区深链不应触发多余折叠展开");

        var expectedFolded = new (string Control, string Fold)[]
        {
            ("RecommendedClearLookRow", SettingsFoldRules.AppearanceMoreFold),
            ("ItemSurfacePanel", SettingsFoldRules.AppearanceMoreFold),
            ("CardShadowSelector", SettingsFoldRules.AppearanceMoreFold),
            ("CardSpacingSelector", SettingsFoldRules.AppearanceMoreFold),
            ("GridSnapCheckBox", SettingsFoldRules.AppearanceMoreFold),
            ("HeaderToolsOnHoverCheckBox", SettingsFoldRules.AppearanceMoreFold),
            ("ClockDividerRow", SettingsFoldRules.ComponentMoreFold),
            ("FileBoxCollectPanel", SettingsFoldRules.SelectedWidgetMoreFold),
            ("FileBoxAutomationCheckBox", SettingsFoldRules.SelectedWidgetMoreFold),
            ("FileBoxDeleteOriginalsCheckBox", SettingsFoldRules.SelectedWidgetMoreFold),
            ("LauncherFavoriteModeComboBox", SettingsFoldRules.LauncherMoreFold),
            ("DockStartMenuButtonCheckBox", SettingsFoldRules.DockAdvancedFold),
            ("DockLauncherButtonCheckBox", SettingsFoldRules.DockAdvancedFold),
            ("DockShowDesktopButtonCheckBox", SettingsFoldRules.DockAdvancedFold),
            ("DockBottomGapSlider", SettingsFoldRules.DockAdvancedFold),
            ("ToggleWidgetsHotkeyButton", SettingsFoldRules.GeneralMoreFold),
            ("QuickTodoHotkeyButton", SettingsFoldRules.GeneralMoreFold),
            ("ToggleDesktopIconsHotkeyButton", SettingsFoldRules.GeneralMoreFold),
            ("BackupList", SettingsFoldRules.GeneralMoreFold),
            ("DataDirectoryText", SettingsFoldRules.GeneralMoreFold),
            ("UninstallRow", SettingsFoldRules.GeneralMoreFold)
        };
        foreach (var (control, expectedFold) in expectedFolded)
        {
            Check(SettingsFoldRules.TryGetFoldForSetting(control, out var actualFold) && actualFold == expectedFold,
                $"被折叠控件 {control} 应映射到 {expectedFold}");
        }

        // 上提到 Dock 常驻区的设置不应继续归属折叠区。
        Check(!SettingsFoldRules.TryGetFoldForSetting("DockIconSizeSlider", out _)
                && !SettingsFoldRules.TryGetFoldForSetting("DockHoverMagnificationCheckBox", out _),
            "上提到 Dock 常驻区的图标大小与悬停放大不应映射到折叠区");

        // 移出「更多选项」上提到桌面组件详情顶部固定操作栏的按钮不应继续归属折叠区。
        Check(!SettingsFoldRules.TryGetFoldForSetting("DeleteComponentButton", out _)
                && !SettingsFoldRules.TryGetFoldForSetting("ResetComponentPlacementButton", out _)
                && !SettingsFoldRules.TryGetFoldForSetting("ResetWidgetPlacementButton", out _)
                && !SettingsFoldRules.TryGetFoldForSetting("DeleteWidgetButton", out _),
            "上提到桌面组件顶部操作栏的恢复默认位置与删除按钮不应映射到折叠区");

        // 移入外观页「各部分外观」手风琴的三处外观控件不应继续归属原页折叠区，而应由 AppearancePartLookRules 解析。
        var movedAppearanceControls = new[]
        {
            "LauncherLookSettingsRow", "LauncherFollowLookRadio", "LauncherCustomLookRadio",
            "LauncherCustomLookPanel", "LauncherColorEditor", "LauncherOpacitySlider", "LauncherCornerRadiusSlider",
            "DockLookSettingsRow", "DockFollowLookRadio", "DockCustomLookRadio",
            "DockCustomLookPanel", "DockColorEditor", "DockOpacitySlider",
            "TopBarFrostedCheckBox", "TopBarLookSettingsRow", "TopBarFollowLookRadio", "TopBarCustomLookRadio",
            "TopBarCustomLookPanel", "TopBarColorEditor", "TopBarOpacitySlider"
        };
        foreach (var moved in movedAppearanceControls)
        {
            Check(!SettingsFoldRules.TryGetFoldForSetting(moved, out _),
                $"已移入外观页的控件 {moved} 不应再映射到原页面折叠区");
            Check(AppearancePartLookRules.ResolvePartForControl(moved) is not null,
                $"已移入外观页的控件 {moved} 应能解析到对应手风琴部分");
        }

        // 搜索命中时，合并逻辑祖先与映射表并按从外到内去重。
        var resolved = SettingsFoldRules.ResolveFoldsToExpand(
            "GridSnapCheckBox",
            [SettingsFoldRules.AppearanceMoreFold]);
        Check(resolved.SequenceEqual([SettingsFoldRules.AppearanceMoreFold]),
            "相同折叠区应去重");

        var resolvedByMapOnly = SettingsFoldRules.ResolveFoldsToExpand("BackupList", []);
        Check(resolvedByMapOnly.SequenceEqual([SettingsFoldRules.GeneralMoreFold]),
            "即使未传入逻辑祖先也应按映射表解析目标折叠区");

        var resolvedFoldSelf = SettingsFoldRules.ResolveFoldsToExpand(
            SettingsFoldRules.GeneralMoreFold,
            [],
            targetIsFold: true);
        Check(resolvedFoldSelf.SequenceEqual([SettingsFoldRules.GeneralMoreFold]),
            "目标本身为折叠区时应展开自身");

        var resolvedAlwaysVisible = SettingsFoldRules.ResolveFoldsToExpand("CleanDesktopRow", []);
        Check(resolvedAlwaysVisible.Count == 0,
            "常驻区设置不应要求展开任何折叠区");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
