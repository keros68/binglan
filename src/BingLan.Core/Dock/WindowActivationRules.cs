namespace BingLan.Core.Dock;

public sealed record WindowCandidateInfo(
    nint Handle,
    bool IsMinimized,
    bool IsEnabled,
    bool HasEnabledPopup,
    bool HasTitle)
{
    public bool CanActivateDirectly => IsEnabled || HasEnabledPopup;
}

public static class WindowActivationRules
{
    public static bool ShouldIncludeTopLevelWindow(
        bool isAppWindow,
        bool isToolWindow,
        bool isNoActivate,
        bool isCloaked,
        bool isMinimized,
        bool isLayeredClickThrough,
        bool isDisabled,
        bool hasTitle,
        int width,
        int height)
    {
        if (!isAppWindow && (isToolWindow || isNoActivate))
        {
            return false;
        }

        if (isCloaked)
        {
            return false;
        }

        if (isAppWindow)
        {
            return true;
        }

        // Windows moves or collapses minimized windows off-screen, so size and auxiliary
        // style checks only apply while a window is not minimized.
        if (!isMinimized)
        {
            if (width <= 1 || height <= 1)
            {
                return false;
            }

            if (isLayeredClickThrough)
            {
                return false;
            }

            if (isDisabled && !hasTitle)
            {
                return false;
            }
        }

        return true;
    }

    public static WindowCandidateInfo? SelectActivationTarget(
        IReadOnlyList<WindowCandidateInfo> liveWindows,
        Func<nint, int>? attentionPriority = null)
    {
        ArgumentNullException.ThrowIfNull(liveWindows);
        if (liveWindows.Count == 0)
        {
            return null;
        }

        if (attentionPriority is not null)
        {
            var activatable = liveWindows.Where(w => w.CanActivateDirectly).ToArray();
            if (activatable.Length > 0)
            {
                var exactFlashing = activatable.Where(w => attentionPriority(w.Handle) >= 2).ToArray();
                if (exactFlashing.Length > 0)
                {
                    return PickPreferredCandidate(exactFlashing);
                }

                var processFlashing = activatable.Where(w => attentionPriority(w.Handle) >= 1).ToArray();
                if (processFlashing.Length > 0)
                {
                    return PickPreferredCandidate(processFlashing);
                }
            }
        }

        return PickPreferredCandidate(liveWindows);
    }

    private static WindowCandidateInfo PickPreferredCandidate(IReadOnlyList<WindowCandidateInfo> candidates) =>
        candidates.FirstOrDefault(w => !w.IsMinimized && w.CanActivateDirectly && w.HasTitle)
        ?? candidates.FirstOrDefault(w => !w.IsMinimized && w.CanActivateDirectly)
        ?? candidates.FirstOrDefault(w => w.IsMinimized && w.CanActivateDirectly && w.HasTitle)
        ?? candidates.FirstOrDefault(w => w.IsMinimized && w.CanActivateDirectly)
        ?? candidates[0];

    public static string FormatSwitchBlockedMessage(string displayName, bool targetElevated, bool foregroundElevated)
    {
        var name = string.IsNullOrWhiteSpace(displayName) ? "该应用" : displayName;
        if (targetElevated)
        {
            return $"{name} 正在以管理员身份运行，Windows 不允许普通权限的 Dock 切换它。";
        }

        if (foregroundElevated)
        {
            return $"当前前台是管理员程序，Windows 阻止了切换到 {name}，已闪烁提醒";
        }

        return $"Windows 阻止了切换到 {name}，已闪烁提醒";
    }

    public static string FormatStartMenuBlockedMessage(bool taskbarElevated, bool foregroundElevated)
    {
        if (taskbarElevated)
        {
            return "Windows 资源管理器正在以管理员身份运行，普通权限的 Dock 无法打开开始菜单，请按键盘 Win 键。";
        }

        if (foregroundElevated)
        {
            return "当前前台是管理员程序，Windows 阻止了打开开始菜单。请先切换到其他窗口或按键盘 Win 键。";
        }

        return "无法打开 Windows 开始菜单，请稍后重试。";
    }
}
