namespace BingLan.Core.Models;

public enum DesktopMode
{
    WindowsNative,
    IceBlueHybrid,
    AppleStyle
}

/// <summary>
/// The three desktop modes are presets over two independent settings, the ice-blue
/// dock and the native taskbar. Choosing a mode writes safe defaults to both; either
/// can still be changed on its own afterwards.
/// </summary>
public static class DesktopModeRules
{
    /// <summary>
    /// The three information cards whose data the top bar also carries; the apple mode
    /// hides them so the same facts do not show twice.
    /// </summary>
    public static readonly DesktopComponentKind[] TopBarInfoCardKinds =
    [
        DesktopComponentKind.TimeDate,
        DesktopComponentKind.Weather,
        DesktopComponentKind.Performance
    ];

    public static void Apply(DesktopMode mode, DockState dock, TaskbarState taskbar)
    {
        ArgumentNullException.ThrowIfNull(dock);
        ArgumentNullException.ThrowIfNull(taskbar);

        switch (mode)
        {
            case DesktopMode.IceBlueHybrid:
                dock.IsEnabled = true;
                dock.VisibilityMode = DockVisibilityMode.ReserveWorkArea;
                taskbar.Mode = TaskbarMode.Transparent;
                break;
            case DesktopMode.AppleStyle:
                dock.IsEnabled = true;
                dock.VisibilityMode = DockVisibilityMode.ReserveWorkArea;
                taskbar.Mode = TaskbarMode.SmartHide;
                break;
            default:
                dock.IsEnabled = false;
                taskbar.Mode = TaskbarMode.SystemDefault;
                break;
        }
    }

    /// <summary>
    /// The full orchestration: the dock and taskbar defaults from the two-module Apply,
    /// plus the top bar and the information cards it replaces. The top bar never joins
    /// Detect: turning it on or off on its own is the same kind of drift as changing the
    /// taskbar mode, and the settings page shows it that way.
    /// </summary>
    public static void Apply(
        DesktopMode mode,
        DockState dock,
        TaskbarState taskbar,
        TopBarState topBar,
        DesktopExperienceState experience)
    {
        ArgumentNullException.ThrowIfNull(topBar);
        ArgumentNullException.ThrowIfNull(experience);

        Apply(mode, dock, taskbar);
        topBar.IsEnabled = mode == DesktopMode.AppleStyle;
        foreach (var kind in TopBarInfoCardKinds)
        {
            experience.GetComponent(kind).IsVisible = mode != DesktopMode.AppleStyle;
        }
    }

    /// <summary>The mode matching the current settings, or null when they were customised.</summary>
    public static DesktopMode? Detect(DockState dock, TaskbarState taskbar)
    {
        ArgumentNullException.ThrowIfNull(dock);
        ArgumentNullException.ThrowIfNull(taskbar);

        return (dock.IsEnabled, taskbar.Mode) switch
        {
            (false, TaskbarMode.SystemDefault) => DesktopMode.WindowsNative,
            (true, TaskbarMode.Transparent or TaskbarMode.Blur) => DesktopMode.IceBlueHybrid,
            (true, TaskbarMode.SmartHide) => DesktopMode.AppleStyle,
            _ => null
        };
    }

    /// <summary>
    /// The mode the current settings start from, and what was changed on the Dock,
    /// taskbar or top bar pages since. The Dock and the top bar shape the desktop and
    /// weigh twice a taskbar style; the lowest weight wins, ties in the order ice-blue
    /// hybrid, apple, Windows native. Without a top bar state the top bar is not compared.
    /// </summary>
    public static DesktopModeMatch Match(DockState dock, TaskbarState taskbar, TopBarState? topBar)
    {
        ArgumentNullException.ThrowIfNull(dock);
        ArgumentNullException.ThrowIfNull(taskbar);

        DesktopModeMatch? best = null;
        var bestWeight = int.MaxValue;
        foreach (var mode in new[] { DesktopMode.IceBlueHybrid, DesktopMode.AppleStyle, DesktopMode.WindowsNative })
        {
            var differences = Differences(mode, dock, taskbar, topBar, out var weight, out var changed);
            if (weight < bestWeight)
            {
                best = new DesktopModeMatch(mode, differences)
                {
                    DockChanged = changed.Dock,
                    TaskbarChanged = changed.Taskbar,
                    TopBarChanged = changed.TopBar
                };
                bestWeight = weight;
            }
        }

        return best!;
    }

    private static List<string> Differences(
        DesktopMode mode,
        DockState dock,
        TaskbarState taskbar,
        TopBarState? topBar,
        out int weight,
        out (bool Dock, bool Taskbar, bool TopBar) changed)
    {
        var differences = new List<string>();
        weight = 0;
        changed = default;
        var dockOn = mode != DesktopMode.WindowsNative;
        if (dock.IsEnabled != dockOn)
        {
            differences.Add(dock.IsEnabled ? "开启了 Dock" : "关闭了 Dock");
            weight += 2;
            changed.Dock = true;
        }

        var taskbarMatches = mode switch
        {
            DesktopMode.IceBlueHybrid => taskbar.Mode is TaskbarMode.Transparent or TaskbarMode.Blur,
            DesktopMode.AppleStyle => taskbar.Mode == TaskbarMode.SmartHide,
            _ => taskbar.Mode == TaskbarMode.SystemDefault
        };
        if (!taskbarMatches)
        {
            differences.Add($"任务栏改为{GetTaskbarName(taskbar.Mode)}");
            weight += 1;
            changed.Taskbar = true;
        }

        if (topBar is not null && topBar.IsEnabled != (mode == DesktopMode.AppleStyle))
        {
            differences.Add(topBar.IsEnabled ? "开启了顶端信息条" : "关闭了顶端信息条");
            weight += 2;
            changed.TopBar = true;
        }

        return differences;
    }

    public static string GetTaskbarName(TaskbarMode mode) => mode switch
    {
        TaskbarMode.Transparent => "透明",
        TaskbarMode.Blur => "模糊",
        TaskbarMode.SmartHide => "自动隐藏",
        _ => "系统默认"
    };

    public static string GetName(DesktopMode mode) => mode switch
    {
        DesktopMode.IceBlueHybrid => "冰蓝混合",
        DesktopMode.AppleStyle => "苹果式",
        _ => "Windows 原生"
    };
}

/// <summary>A desktop mode and the settings changed away from it.</summary>
public sealed record DesktopModeMatch(DesktopMode Mode, IReadOnlyList<string> Differences)
{
    public bool IsExact => Differences.Count == 0;

    /// <summary>Whether the Dock, the taskbar or the top bar differs from the mode's defaults.</summary>
    public bool DockChanged { get; init; }

    public bool TaskbarChanged { get; init; }

    public bool TopBarChanged { get; init; }
}
