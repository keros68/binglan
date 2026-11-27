namespace BingLan.Core.Dock;

/// <summary>
/// Keeps evaluating the prior application while a BingLan panel temporarily holds
/// the foreground for keyboard input. A different foreground window takes effect
/// immediately when the user switches away or opens an application from the panel.
/// </summary>
public static class DockForegroundRules
{
    public static nint EffectiveForeground(nint foreground, nint panel, nint borrowed) =>
        panel != 0 && borrowed != 0 && borrowed != panel && foreground == panel
            ? borrowed
            : foreground;
}
