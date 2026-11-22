using BingLan.Core.Models;

namespace BingLan.SmokeTests;

internal static class SettingsQuickActionsTests
{
    public static void Run()
    {
        var items = SettingsQuickActions.Items;
        Check(SettingsQuickActions.SectionTitle == "我想…", "首页快捷入口分区标题应为“我想…”");
        Check(items.Count == 6, $"首页「我想…」入口应为 6 项，实际 {items.Count} 项");

        var expectedLabels = new[]
        {
            "换个外观风格",
            "开关 Dock 或顶栏",
            "设置天气城市",
            "修改快捷键",
            "隐藏桌面图标",
            "整理桌面文件"
        };
        Check(items.Select(item => item.Label).SequenceEqual(expectedLabels),
            "6 个「我想…」入口文案与顺序应与设计一致");

        var validPages = new HashSet<string>(
            SettingsNavigationRules.Pages.Select(page => page.PageKey),
            StringComparer.Ordinal);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            Check(!string.IsNullOrWhiteSpace(item.Key), "入口 Key 不能为空");
            Check(!string.IsNullOrWhiteSpace(item.Label), $"入口 {item.Key} 文案不能为空");
            Check(!string.IsNullOrWhiteSpace(item.Glyph), $"入口 {item.Key} 图标不能为空");
            Check(!string.IsNullOrWhiteSpace(item.AutomationName)
                    && item.AutomationName.Contains(item.Label, StringComparison.Ordinal),
                $"入口 {item.Key} 应提供包含可见文案的无障碍名称");
            Check(!string.IsNullOrWhiteSpace(item.TargetKey), $"入口 {item.Key} 目标控件键不能为空");
            Check(validPages.Contains(item.ExpectedPageKey),
                $"入口 {item.Key} 目标页面 {item.ExpectedPageKey} 必须是 8 个有效设置页之一");
            Check(seenKeys.Add(item.Key), $"入口 Key 不得重复：{item.Key}");
            Check(seenTargets.Add(item.TargetKey), $"入口目标控件键不得重复：{item.TargetKey}");
            Check(ReferenceEquals(SettingsQuickActions.FindByKey(item.Key), item),
                $"FindByKey({item.Key}) 应返回对应条目");
        }

        Check(SettingsQuickActions.FindByKey(null) is null
                && SettingsQuickActions.FindByKey("") is null
                && SettingsQuickActions.FindByKey("UnknownKey") is null,
            "未知或空 Key 的 FindByKey 应返回 null");

        var toggleBars = SettingsQuickActions.FindByKey(SettingsQuickActions.ToggleBarsKey);
        Check(toggleBars is { ExpectedPageKey: SettingsNavigationRules.DockPageKey, ExpectedScope: null, TargetKey: "DockEnabledRow" },
            "开关 Dock 或顶栏应直接指向独立 Dock 页且不再使用旧 Bar:Dock 范围");

        var weather = SettingsQuickActions.FindByKey(SettingsQuickActions.SetWeatherCityKey);
        Check(weather is { ExpectedPageKey: "Components", ExpectedScope: "Kind:Weather", FallbackTargetKey: "DesktopComponentList" },
            "设置天气城市应指向桌面组件页天气范围并提供组件列表回退");

        var organize = SettingsQuickActions.FindByKey(SettingsQuickActions.OrganizeDesktopFilesKey);
        Check(organize is
            {
                ExpectedPageKey: "Components",
                ExpectedScope: "FileBox",
                FallbackTargetKey: "AddFileBoxButton"
            }
            && !string.IsNullOrWhiteSpace(organize.FallbackStatusMessage)
            && organize.FallbackStatusMessage.Contains("文件收纳盒", StringComparison.Ordinal)
            && !organize.FallbackStatusMessage.Contains("桌面分组盒", StringComparison.Ordinal),
            "整理桌面文件应指向文件收纳盒设置，并在无文件收纳盒时回退到新建文件收纳盒按钮并给出新术语提示");

        Check(SettingsQuickActions.UnavailableStatusMessage == "暂时无法跳转到这项设置",
            "快捷入口不可跳转提示文案应与约定一致");
        Check(SettingsQuickActions.MaxRevealRetries == 2,
            "延迟定位重试次数应为最多 2 次");

        // 纯逻辑：无回退入口在搜索条目不可用时仍应回落 JumpToPrimaryTarget，而不是返回 Unavailable。
        var appearance = SettingsQuickActions.FindByKey(SettingsQuickActions.ChangeAppearanceKey)!;
        Check(SettingsQuickActions.ResolveExecution(
                appearance,
                hasPrimaryTarget: true,
                hasPrimarySearchEntry: true,
                isPrimarySearchEntryAvailable: true,
                hasFallbackTarget: false) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.OpenPrimarySearchEntry, false),
            "无回退入口且搜索条目可用时应走 OpenPrimarySearchEntry");
        Check(SettingsQuickActions.ResolveExecution(
                appearance,
                hasPrimaryTarget: true,
                hasPrimarySearchEntry: true,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: false) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.JumpToPrimaryTarget, false),
            "无回退入口在搜索条目不可用但主目标存在时应走 JumpToPrimaryTarget");
        Check(SettingsQuickActions.ResolveExecution(
                appearance,
                hasPrimaryTarget: true,
                hasPrimarySearchEntry: false,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: false) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.JumpToPrimaryTarget, false),
            "无回退入口在无搜索条目但主目标存在时应走 JumpToPrimaryTarget");
        Check(SettingsQuickActions.ResolveExecution(
                appearance,
                hasPrimaryTarget: false,
                hasPrimarySearchEntry: false,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: false) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.Unavailable, false),
            "无回退入口且主目标不存在时应返回 Unavailable");

        // 纯逻辑：整理桌面文件的回退仅在真实无文件收纳盒（isPrimaryDomainAvailable == false）时触发。
        Check(SettingsQuickActions.ResolveExecution(
                organize!,
                hasPrimaryTarget: true,
                hasPrimarySearchEntry: true,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: true,
                isPrimaryDomainAvailable: false) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.JumpToFallbackTarget, true),
            "无文件收纳盒时整理桌面文件应回退到新建按钮并展示回退提示");
        Check(SettingsQuickActions.ResolveExecution(
                organize!,
                hasPrimaryTarget: true,
                hasPrimarySearchEntry: true,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: true,
                isPrimaryDomainAvailable: true) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.JumpToPrimaryTarget, false),
            "已有文件收纳盒但搜索条目不可用时应优先尝试主目标，不得回退或显示无收纳盒提示");
        Check(SettingsQuickActions.ResolveExecution(
                organize!,
                hasPrimaryTarget: false,
                hasPrimarySearchEntry: false,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: true,
                isPrimaryDomainAvailable: true) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.Unavailable, false),
            "已有文件收纳盒但主目标解析失败时不得误用无收纳盒回退");
        Check(SettingsQuickActions.ResolveExecution(
                organize!,
                hasPrimaryTarget: false,
                hasPrimarySearchEntry: false,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: false,
                isPrimaryDomainAvailable: false) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.Unavailable, false),
            "无文件收纳盒且回退按钮也不存在时应返回 Unavailable");

        // 纯逻辑：设置天气城市先尝试主目标，仅天气组件不存在或主目标解析失败时回退组件列表且不显示空域提示。
        Check(SettingsQuickActions.ResolveExecution(
                weather!,
                hasPrimaryTarget: true,
                hasPrimarySearchEntry: true,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: true,
                isPrimaryDomainAvailable: true) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.JumpToPrimaryTarget, false),
            "天气组件存在且主目标已解析时即使条目不可用也应先尝试主目标");
        Check(SettingsQuickActions.ResolveExecution(
                weather!,
                hasPrimaryTarget: false,
                hasPrimarySearchEntry: false,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: true,
                isPrimaryDomainAvailable: true) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.JumpToFallbackTarget, false),
            "天气主目标无法解析时应回退到桌面组件列表且不显示空域提示");
        Check(SettingsQuickActions.ResolveExecution(
                weather!,
                hasPrimaryTarget: true,
                hasPrimarySearchEntry: true,
                isPrimarySearchEntryAvailable: false,
                hasFallbackTarget: true,
                isPrimaryDomainAvailable: false) == new SettingsQuickActionResolution(SettingsQuickActionExecutionMode.JumpToFallbackTarget, false),
            "天气组件不存在时应回退到桌面组件列表");

        // 纯逻辑：延迟定位请求代次递增与回调判定。
        var gen1 = SettingsQuickActions.NextRevealGeneration(0);
        var gen2 = SettingsQuickActions.NextRevealGeneration(gen1);
        Check(gen1 == 1 && gen2 == 2 && SettingsQuickActions.NextRevealGeneration(-5) == 1,
            "延迟定位请求代次应严格递增并忽略非法负值");
        Check(!SettingsQuickActions.IsCurrentRevealGeneration(0, 0)
                && !SettingsQuickActions.IsCurrentRevealGeneration(gen1, gen2)
                && SettingsQuickActions.IsCurrentRevealGeneration(gen2, gen2),
            "只有非零且等于最新代次的请求才应视为当前有效");
        Check(SettingsQuickActions.EvaluateRevealAttempt(gen1, gen2, isWindowClosed: false, isTargetVisible: true, remainingRetries: 2)
                == SettingsRevealAction.Abort,
            "旧代次回调即使目标可见也必须放弃");
        Check(SettingsQuickActions.EvaluateRevealAttempt(gen2, gen2, isWindowClosed: true, isTargetVisible: true, remainingRetries: 2)
                == SettingsRevealAction.Abort,
            "窗口已关闭时回调必须放弃");
        Check(SettingsQuickActions.EvaluateRevealAttempt(gen2, gen2, isWindowClosed: false, isTargetVisible: true, remainingRetries: 0)
                == SettingsRevealAction.Complete,
            "最新代次且目标可见时应完成定位");
        Check(SettingsQuickActions.EvaluateRevealAttempt(gen2, gen2, isWindowClosed: false, isTargetVisible: false, remainingRetries: 2)
                == SettingsRevealAction.Retry
                && SettingsQuickActions.EvaluateRevealAttempt(gen2, gen2, isWindowClosed: false, isTargetVisible: false, remainingRetries: 1)
                == SettingsRevealAction.Retry,
            "最新代次且目标尚不可见且仍有重试次数时应继续重试（最多 2 次）");
        Check(SettingsQuickActions.EvaluateRevealAttempt(gen2, gen2, isWindowClosed: false, isTargetVisible: false, remainingRetries: 0)
                == SettingsRevealAction.GiveUp,
            "重试次数耗尽仍不可见时应放弃");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
