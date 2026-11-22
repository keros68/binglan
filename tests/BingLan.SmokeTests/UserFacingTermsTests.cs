using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.TopBar;

namespace BingLan.SmokeTests;

internal static class UserFacingTermsTests
{
    public static void Run()
    {
        var entries = UserFacingTerms.Entries;
        Check(entries.Count >= 12, $"术语映射表应完整覆盖计划条目，实际 {entries.Count} 条");

        var requiredLegacyTerms = new[]
        {
            "冰蓝总览卡",
            "桌面分组盒",
            "分组盒",
            "临时速览桌面分组盒",
            "顶栏与底栏",
            "桌面组件",
            "顶端信息条",
            "清爽桌面",
            "无底板",
            "吸附间距",
            "实时透视",
            "静态壁纸底衬",
            "弹出时切到桌面"
        };
        foreach (var legacy in requiredLegacyTerms)
        {
            Check(UserFacingTerms.FindByLegacyTerm(legacy) is not null,
                $"术语映射表缺少必需旧术语：{legacy}");
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenLegacy = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            Check(!string.IsNullOrWhiteSpace(entry.Key), "术语条目 Key 不能为空");
            Check(seenKeys.Add(entry.Key), $"术语条目 Key 不得重复：{entry.Key}");
            Check(!string.IsNullOrWhiteSpace(entry.LegacyTerm), $"术语 {entry.Key} 的旧术语不能为空");
            Check(seenLegacy.Add(entry.LegacyTerm), $"术语旧称不得重复：{entry.LegacyTerm}");
            Check(!string.IsNullOrWhiteSpace(entry.DisplayText), $"术语 {entry.Key} 的新文案不能为空");
            Check(!UserFacingTerms.ContainsInternalIdentifier(entry.DisplayText),
                $"术语 {entry.Key} 的新文案不得包含内部英文标识：{entry.DisplayText}");
            Check(!string.IsNullOrWhiteSpace(entry.DescriptionHint), $"术语 {entry.Key} 的白话说明不能为空");
            Check(!UserFacingTerms.ContainsInternalIdentifier(entry.DescriptionHint),
                $"术语 {entry.Key} 的白话说明不得包含内部英文标识：{entry.DescriptionHint}");
            Check(entry.SearchSynonyms.Count > 0, $"术语 {entry.Key} 必须保留搜索同义词");

            var seenSynonyms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var synonym in entry.SearchSynonyms)
            {
                Check(!string.IsNullOrWhiteSpace(synonym), $"术语 {entry.Key} 的搜索同义词不能为空白");
                Check(seenSynonyms.Add(synonym), $"术语 {entry.Key} 存在重复同义词：{synonym}");
            }

            Check(entry.SearchSynonyms.Contains(entry.LegacyTerm, StringComparer.Ordinal),
                $"术语 {entry.Key} 的旧术语“{entry.LegacyTerm}”必须保留在搜索同义词中");
            Check(ReferenceEquals(UserFacingTerms.FindByKey(entry.Key), entry),
                $"FindByKey({entry.Key}) 应返回同一条目");
            Check(ReferenceEquals(UserFacingTerms.FindByLegacyTerm(entry.LegacyTerm), entry),
                $"FindByLegacyTerm({entry.LegacyTerm}) 应返回同一条目");
        }

        Check(UserFacingTerms.FindByKey(null) is null && UserFacingTerms.FindByLegacyTerm("") is null,
            "空查找应返回 null");

        // Internal identifier guard checks
        Check(UserFacingTerms.ContainsInternalIdentifier("FileBoxWindow"), "应拦截 PascalCase 内部类型名");
        Check(UserFacingTerms.ContainsInternalIdentifier("CleanDesktopEnabled"), "应拦截内部属性名");
        Check(UserFacingTerms.ContainsInternalIdentifier("LauncherBackdropMode"), "应拦截内部枚举名");
        Check(UserFacingTerms.ContainsInternalIdentifier("top_bar_state"), "应拦截 snake_case 标识");
        Check(!UserFacingTerms.ContainsInternalIdentifier("隐藏 Windows 桌面图标"), "不应误拦 Windows");
        Check(!UserFacingTerms.ContainsInternalIdentifier("顶栏与底栏 · Dock 与任务栏"), "不应误拦 Dock");

        // Cross-check core runtime strings stay synchronized with UserFacingTerms
        var peekTerm = UserFacingTerms.FindByKey(UserFacingTerms.PeekFileBoxesKey)!;
        Check(GlobalHotkeyRules.DisplayName(GlobalHotkeyKind.PeekFileBoxes) == peekTerm.DisplayText,
            "快捷键显示名应与术语映射表中的临时浮起文件收纳盒一致");

        var fileBoxTerm = UserFacingTerms.FindByKey(UserFacingTerms.DesktopFileBoxKey)!;
        var organizeAction = SettingsQuickActions.FindByKey(SettingsQuickActions.OrganizeDesktopFilesKey)!;
        Check(organizeAction.FallbackStatusMessage is not null
                && organizeAction.FallbackStatusMessage.Contains(fileBoxTerm.DisplayText, StringComparison.Ordinal)
                && !organizeAction.FallbackStatusMessage.Contains("桌面分组盒", StringComparison.Ordinal),
            "「我想…」整理桌面文件回退提示应使用新术语“文件收纳盒”");

        var deleteRuleText = OriginalFileDeleteRules.Describe(OriginalFileDeleteBlock.ProtectedLocation);
        Check(deleteRuleText.Contains(fileBoxTerm.DisplayText, StringComparison.Ordinal),
            "删除原文件拒绝说明应使用新术语“文件收纳盒”");

        var clearLookSummary = DesktopStyleRules.DescribeSharedLookSummary(new DesktopStyleState { Glass = GlassMode.Clear });
        var clearTerm = UserFacingTerms.FindByKey(UserFacingTerms.ClearGlassKey)!;
        Check(clearLookSummary.StartsWith(clearTerm.DisplayText, StringComparison.Ordinal),
            "全局外观摘要应使用新术语“纯透明（无底板）”");

        var topBarTerm = UserFacingTerms.FindByKey(UserFacingTerms.TopBarKey)!;
        var drift = DesktopModeRules.Match(new DockState { IsEnabled = false }, new TaskbarState(), new TopBarState { IsEnabled = true });
        Check(drift.Differences.Any(diff => diff.Contains(topBarTerm.DisplayText, StringComparison.Ordinal)),
            "桌面方案偏离摘要应使用新术语“顶部信息条”");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
