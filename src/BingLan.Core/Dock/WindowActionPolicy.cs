namespace BingLan.Core.Dock;

public enum DockClickAction
{
    MinimizeForeground,
    Activate,
    RestoreAndActivate
}

public static class WindowActionPolicy
{
    public static DockClickAction Decide(bool groupContainsForeground, bool foregroundIsMinimized, bool targetIsMinimized)
    {
        // The foreground pointer keeps naming a window for a moment after the dock
        // minimized it, so a quick second click must restore, not minimize again.
        if (groupContainsForeground && !foregroundIsMinimized)
        {
            return DockClickAction.MinimizeForeground;
        }
        return targetIsMinimized
            ? DockClickAction.RestoreAndActivate
            : DockClickAction.Activate;
    }
}
