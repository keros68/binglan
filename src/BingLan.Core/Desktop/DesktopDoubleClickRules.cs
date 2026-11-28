namespace BingLan.Core.Desktop;

/// <summary>
/// Snapshot of the last unmatched primary mouse button press in physical screen pixels,
/// kept purely in memory so double-click detection can be tested deterministically
/// without calling Win32 APIs in Core.
/// </summary>
public readonly record struct DesktopClickStamp(int X, int Y, long TimestampMs);

public readonly record struct DesktopDoubleClickDecision(
    bool IsDoubleClick,
    DesktopClickStamp? NextPendingClick);

/// <summary>
/// Pure timing, distance, desktop window hierarchy, and MSAA blank-area rules for
/// toggling desktop icons by left double-clicking an empty area of the desktop.
/// </summary>
public static class DesktopDoubleClickRules
{
    public const int DefaultDoubleClickTimeMs = 500;
    public const int DefaultDoubleClickWidth = 4;
    public const int DefaultDoubleClickHeight = 4;

    public const int ChildIdSelf = 0;

    // Standard MSAA object role constants (oleacc.h).
    public const int RoleSystemWindow = 0x09;
    public const int RoleSystemClient = 0x0A;
    public const int RoleSystemPane = 0x10;
    public const int RoleSystemList = 0x21;
    public const int RoleSystemListItem = 0x22;

    /// <summary>Expands the 32-bit Win32 message time into the current 64-bit uptime.</summary>
    public static long ExpandMessageTimestamp(uint messageTime, long receivedAtMs) =>
        receivedAtMs - unchecked((uint)receivedAtMs - messageTime);

    /// <summary>
    /// Evaluates one left-button-down event against the previous unmatched press.
    /// After a matched double-click the pending press is cleared so a third rapid press
    /// begins a fresh sequence instead of chaining into a second trigger.
    /// </summary>
    public static DesktopDoubleClickDecision EvaluateLeftButtonDown(
        DesktopClickStamp? previousClick,
        int x,
        int y,
        long timestampMs,
        int maxIntervalMs,
        int doubleClickWidth,
        int doubleClickHeight)
    {
        var current = new DesktopClickStamp(x, y, timestampMs);
        if (previousClick is not { } first)
        {
            return new DesktopDoubleClickDecision(false, current);
        }

        if (maxIntervalMs <= 0 || doubleClickWidth < 0 || doubleClickHeight < 0)
        {
            return new DesktopDoubleClickDecision(false, current);
        }

        var elapsed = timestampMs - first.TimestampMs;
        if (elapsed < 0 || elapsed > maxIntervalMs)
        {
            return new DesktopDoubleClickDecision(false, current);
        }

        // Windows SM_CXDOUBLECLK / SM_CYDOUBLECLK specify the full width/height of the
        // rectangle centered on the first click: |dx| <= cx / 2 and |dy| <= cy / 2.
        var maxDeltaX = doubleClickWidth / 2;
        var maxDeltaY = doubleClickHeight / 2;
        var dx = Math.Abs((long)x - first.X);
        var dy = Math.Abs((long)y - first.Y);
        if (dx <= maxDeltaX && dy <= maxDeltaY)
        {
            return new DesktopDoubleClickDecision(true, null);
        }

        return new DesktopDoubleClickDecision(false, current);
    }

    /// <summary>
    /// Whether a leaf-to-root Win32 window class chain from <c>WindowFromPoint</c>
    /// represents the Windows desktop surface (either the icon list/view hosted by
    /// <c>Progman</c>/<c>WorkerW</c>, or the wallpaper host <c>Progman</c>/<c>WorkerW</c>
    /// directly when desktop icons are hidden). Inline rename <c>Edit</c> controls,
    /// Explorer folder windows (<c>CabinetWClass</c>), taskbars and other apps are rejected.
    /// </summary>
    public static bool IsDesktopAncestorChain(IReadOnlyList<string> leafToRootClassNames)
    {
        if (leafToRootClassNames is null || leafToRootClassNames.Count == 0)
        {
            return false;
        }

        var root = leafToRootClassNames[^1];
        if (!IsDesktopHostClass(root))
        {
            return false;
        }

        var leaf = leafToRootClassNames[0];
        if (IsDesktopHostClass(leaf))
        {
            // Direct wallpaper layer: ["Progman"], ["WorkerW"], or ["WorkerW", "Progman"] on Win11 24H2+.
            return true;
        }

        if (string.Equals(leaf, "SHELLDLL_DefView", StringComparison.Ordinal))
        {
            return leafToRootClassNames.Count >= 2;
        }

        if (string.Equals(leaf, "SysListView32", StringComparison.Ordinal))
        {
            for (var index = 1; index < leafToRootClassNames.Count - 1; index++)
            {
                if (string.Equals(leafToRootClassNames[index], "SHELLDLL_DefView", StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool IsDesktopHostClass(string? className) =>
        string.Equals(className, "Progman", StringComparison.Ordinal)
        || string.Equals(className, "WorkerW", StringComparison.Ordinal);

    /// <summary>Whether a validated desktop background hit has a known hidden icon list.</summary>
    public static bool IsDesktopBlankWithHiddenIconList(string windowClass, bool? iconListVisible) =>
        iconListVisible == false
        && (windowClass == "SHELLDLL_DefView" || IsDesktopHostClass(windowClass));

    /// <summary>
    /// Whether an <c>AccessibleObjectFromPoint</c> result positively confirms an empty
    /// desktop surface (not a desktop icon or other child element). Missing, failed, or
    /// unrecognized values are treated conservatively as uncertain and return <c>false</c>.
    /// </summary>
    public static bool IsDesktopBlankAccessibleHit(int? childId, int? role) =>
        childId == ChildIdSelf
        && role is RoleSystemList or RoleSystemClient or RoleSystemWindow or RoleSystemPane;
}
