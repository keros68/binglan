using BingLan.Core.Models;

namespace BingLan.SmokeTests;

internal static class AppearancePartLookRulesTests
{
    public static void Run()
    {
        // 1. 固定四行顺序与元数据
        var parts = AppearancePartLookRules.Parts;
        Check(parts.Count == 4, $"「各部分外观」应固定为 4 行，实际 {parts.Count} 行");
        Check(parts.Select(p => (p.Kind, p.Key, p.Title)).SequenceEqual([
                (AppearancePartKind.DesktopCards, AppearancePartLookRules.CardsPartKey, "桌面卡片"),
                (AppearancePartKind.Launcher, AppearancePartLookRules.LauncherPartKey, "冰蓝总览卡"),
                (AppearancePartKind.Dock, AppearancePartLookRules.DockPartKey, "冰蓝 Dock"),
                (AppearancePartKind.TopBar, AppearancePartLookRules.TopBarPartKey, "顶部信息条")
            ]),
            "「各部分外观」四行顺序、键名或标题不匹配");

        foreach (var part in parts)
        {
            Check(AppearancePartLookRules.GetPartKey(part.Kind) == part.Key,
                $"GetPartKey({part.Kind}) 应返回 {part.Key}");
            Check(AppearancePartLookRules.GetPartTitle(part.Kind) == part.Title,
                $"GetPartTitle({part.Kind}) 应返回 {part.Title}");
            Check(AppearancePartLookRules.TryParsePartKey(part.Key, out var parsed) && parsed == part.Kind,
                $"TryParsePartKey({part.Key}) 应解析为 {part.Kind}");
            Check(AppearancePartLookRules.TryParsePartKey(part.Key.ToLowerInvariant(), out var lowerParsed) && lowerParsed == part.Kind,
                $"TryParsePartKey 应忽略大小写：{part.Key}");
        }
        Check(!AppearancePartLookRules.TryParsePartKey(null, out _)
                && !AppearancePartLookRules.TryParsePartKey("", out _)
                && !AppearancePartLookRules.TryParsePartKey("Unknown", out _),
            "空值或未知键不得解析为有效部分");

        // 2. 行状态判定（跟随全局 / 已自定义 / N 张卡片使用独立外观）
        var dockFollowing = AppearancePartLookRules.EvaluateBinaryPart(AppearancePartKind.Dock, followGlobal: true);
        Check(!dockFollowing.IsCustomized
                && dockFollowing.StatusText == "跟随全局"
                && !dockFollowing.CanRestoreFollow,
            "跟随全局时应显示“跟随全局”且禁用恢复跟随");

        var dockCustom = AppearancePartLookRules.EvaluateBinaryPart(AppearancePartKind.Dock, followGlobal: false);
        Check(dockCustom.IsCustomized
                && dockCustom.StatusText == "已自定义"
                && dockCustom.CanRestoreFollow,
            "已自定义时应显示“已自定义”且启用恢复跟随");

        var cardsAllFollow = AppearancePartLookRules.EvaluateDesktopCardsPart(0);
        Check(!cardsAllFollow.IsCustomized
                && cardsAllFollow.StatusText == "全部跟随全局"
                && !cardsAllFollow.CanRestoreFollow,
            "0 张卡片独立外观时应显示“全部跟随全局”且禁用恢复跟随");

        var cardsNegative = AppearancePartLookRules.EvaluateDesktopCardsPart(-3);
        Check(!cardsNegative.IsCustomized
                && cardsNegative.StatusText == "全部跟随全局"
                && !cardsNegative.CanRestoreFollow,
            "负数卡片计数应钳制为 0");

        var cardsSomeCustom = AppearancePartLookRules.EvaluateDesktopCardsPart(2);
        Check(cardsSomeCustom.IsCustomized
                && cardsSomeCustom.StatusText == "2 张卡片使用独立外观"
                && cardsSomeCustom.CanRestoreFollow,
            "N 张卡片独立外观时应显示“N 张卡片使用独立外观”且启用恢复跟随");

        // 3. 摘要文案与按钮文案
        Check(AppearancePartLookRules.FormatPageSummary(followGlobal: true) == "外观：跟随全局",
            "跟随全局页内摘要应为“外观：跟随全局”");
        Check(AppearancePartLookRules.FormatPageSummary(followGlobal: false) == "外观：已自定义",
            "已自定义页内摘要应为“外观：已自定义”");
        Check(AppearancePartLookRules.FormatCustomizeButtonLabel(isExpanded: false) == "自定义…"
                && AppearancePartLookRules.FormatCustomizeButtonLabel(isExpanded: true) == "收起",
            "手风琴切换按钮文案应区分收起/展开");
        Check(AppearancePartLookRules.FormatViewCardsButtonLabel(0) == "前往桌面卡片页 ›"
                && AppearancePartLookRules.FormatViewCardsButtonLabel(1) == "查看使用独立外观的卡片 ›",
            "桌面卡片摘要区跳转按钮文案应随自定义卡片数量变化");
        Check(AppearancePartLookRules.FormatCardsExpandedSummary("浅色玻璃", 0).Contains("全部", StringComparison.Ordinal) == false
                && AppearancePartLookRules.FormatCardsExpandedSummary("浅色玻璃", 0).Contains("浅色玻璃", StringComparison.Ordinal)
                && AppearancePartLookRules.FormatCardsExpandedSummary("深色玻璃", 3).Contains("3 张卡片", StringComparison.Ordinal),
            "桌面卡片展开摘要应包含全局外观信息与独立卡片数量");

        // 4. 手风琴互斥规则（一次最多展开一行）
        AppearancePartKind? expanded = null;
        expanded = AppearancePartLookRules.ToggleAccordion(expanded, AppearancePartKind.Dock);
        Check(expanded == AppearancePartKind.Dock, "点击收起的行应展开该行");
        expanded = AppearancePartLookRules.ToggleAccordion(expanded, AppearancePartKind.Launcher);
        Check(expanded == AppearancePartKind.Launcher, "点击另一行应展开新行并互斥收起旧行");
        expanded = AppearancePartLookRules.ToggleAccordion(expanded, AppearancePartKind.Launcher);
        Check(expanded is null, "再次点击已展开的行应收起");
        expanded = AppearancePartLookRules.ExpandAccordion(expanded, AppearancePartKind.TopBar);
        Check(expanded == AppearancePartKind.TopBar, "跳转展开应打开目标行");
        expanded = AppearancePartLookRules.ExpandAccordion(expanded, AppearancePartKind.TopBar);
        Check(expanded == AppearancePartKind.TopBar, "跳转已展开的目标行应保持展开而非收起");
        expanded = AppearancePartLookRules.ExpandAccordion(expanded, AppearancePartKind.DesktopCards);
        Check(expanded == AppearancePartKind.DesktopCards, "跳转另一行应互斥切换到目标行");

        // 5. 被移动控件到所属部分的映射（搜索与 JumpToTarget 自动展开依赖）
        var expectedControls = new (string Control, AppearancePartKind Part)[]
        {
            ("AppearanceCardsSummaryRow", AppearancePartKind.DesktopCards),
            ("AppearanceViewCustomCardsButton", AppearancePartKind.DesktopCards),
            ("LauncherLookSettingsRow", AppearancePartKind.Launcher),
            ("LauncherFollowLookRadio", AppearancePartKind.Launcher),
            ("LauncherCustomLookRadio", AppearancePartKind.Launcher),
            ("LauncherCustomLookPanel", AppearancePartKind.Launcher),
            ("LauncherColorEditor", AppearancePartKind.Launcher),
            ("LauncherOpacitySlider", AppearancePartKind.Launcher),
            ("LauncherCornerRadiusSlider", AppearancePartKind.Launcher),
            ("DockFrostedRow", AppearancePartKind.Dock),
            ("DockFrostedCheckBox", AppearancePartKind.Dock),
            ("DockLookSettingsRow", AppearancePartKind.Dock),
            ("DockFollowLookRadio", AppearancePartKind.Dock),
            ("DockCustomLookRadio", AppearancePartKind.Dock),
            ("DockCustomLookPanel", AppearancePartKind.Dock),
            ("DockColorEditor", AppearancePartKind.Dock),
            ("DockOpacitySlider", AppearancePartKind.Dock),
            ("TopBarFrostedCheckBox", AppearancePartKind.TopBar),
            ("TopBarLookSettingsRow", AppearancePartKind.TopBar),
            ("TopBarFollowLookRadio", AppearancePartKind.TopBar),
            ("TopBarCustomLookRadio", AppearancePartKind.TopBar),
            ("TopBarCustomLookPanel", AppearancePartKind.TopBar),
            ("TopBarColorEditor", AppearancePartKind.TopBar),
            ("TopBarOpacitySlider", AppearancePartKind.TopBar)
        };
        foreach (var (control, expectedPart) in expectedControls)
        {
            Check(AppearancePartLookRules.ResolvePartForControl(control) == expectedPart,
                $"控件 {control} 应映射到「各部分外观」的 {expectedPart}");
        }
        Check(AppearancePartLookRules.ResolvePartForControl("CleanDesktopRow") is null
                && AppearancePartLookRules.ResolvePartForControl(null) is null,
            "无关控件或空名不应映射到任何外观手风琴部分");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
