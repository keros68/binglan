using System.Text.Json;
using System.Text.Json.Serialization;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.Core.Taskbar;

public sealed record TaskbarEnvironment(
    int WindowsBuild,
    bool IsHighContrast,
    bool IsRemoteSession,
    bool IsSystemTransparencyEnabled,
    string? CompetingCustomizer,
    int? WindowsUpdateRevision = null);

/// <summary>
/// Whether a mode can be held. <paramref name="Reason"/> is why it cannot, or, when
/// <paramref name="SubstituteMode"/> is set, why that mode is held instead.
/// </summary>
public sealed record TaskbarCompatibilityResult(
    bool IsSupported,
    string Reason,
    TaskbarMode? SubstituteMode = null);

public static class TaskbarCompatibility
{
    public const int MinimumWindows11Build = 22000;

    /// <summary>
    /// Windows 10 draws a Win32 taskbar, so blur and transparency are window accents;
    /// Windows 11 draws a XAML one.
    /// </summary>
    public static bool UsesWin32Taskbar(int windowsBuild) => windowsBuild < MinimumWindows11Build;

    /// <summary>
    /// Windows 11 builds the XAML taskbar background was verified on, live, for the
    /// visual effect, a clean exit, a host crash and an Explorer restart. On other builds
    /// transparent uses the blur accent instead of trusting an unverified XAML structure.
    /// </summary>
    public static readonly IReadOnlySet<int> VerifiedTransparentBuilds = new HashSet<int> { 26200, 26300 };

    public static TaskbarCompatibilityResult Assess(TaskbarMode mode, TaskbarEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        if (mode == TaskbarMode.SystemDefault)
        {
            return new(true, string.Empty);
        }

        if (environment.CompetingCustomizer is { } customizer)
        {
            return new(false, $"{customizer} 正在调整任务栏，请先退出它");
        }

        if (mode == TaskbarMode.SmartHide)
        {
            return new(true, string.Empty);
        }

        if (environment.IsRemoteSession)
        {
            return new(false, "远程桌面中保持系统默认外观");
        }

        if (environment.IsHighContrast)
        {
            return new(false, "对比度主题开启时保持系统默认外观");
        }

        // 26300.9711 passed the XAML lifecycle checks. On 26300.9974 Explorer
        // fail-fasts during diagnostics root enumeration on preview replacement.
        if (mode == TaskbarMode.Transparent && environment.WindowsBuild == 26300
            && environment.WindowsUpdateRevision != 9711)
        {
            return new(true, "此 Windows 修订版暂用模糊，以保持资源管理器稳定", TaskbarMode.Blur);
        }

        if (mode == TaskbarMode.Transparent
            && !UsesWin32Taskbar(environment.WindowsBuild)
            && !VerifiedTransparentBuilds.Contains(environment.WindowsBuild))
        {
            return new(true, "此 Windows 版本暂用模糊代替透明", TaskbarMode.Blur);
        }

        // Transparent and blur need the system transparency effects; the adapter turns
        // them on itself when they are off, so a disabled effect is no longer a reason
        // to refuse here.
        return new(true, string.Empty);
    }
}

/// <summary>
/// What the app changed on the native taskbar, written before the change so a later
/// start (or an uninstaller) can undo it after a crash. It never leaves this machine.
/// </summary>
public sealed class TaskbarCheckpoint
{
    public const int CurrentSchemaVersion = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public bool TransparencyApplied { get; set; }
    public bool AutoHideApplied { get; set; }
    public bool OriginalAutoHide { get; set; }
    public string? XamlSessionName { get; set; }

    /// <summary>Explorer process targeted by the XAML diagnostics connection.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public uint? XamlExplorerProcessId { get; set; }

    /// <summary>The native component reported that it could not restore or detach.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool XamlRestoreFailed { get; set; }

    /// <summary>
    /// The system transparency effects before the app turned them on for transparent
    /// or blur. Null when they were already on and the app changed nothing, so a
    /// restore leaves the user's own value alone.
    /// </summary>
    public bool? OriginalSystemTransparency { get; set; }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static TaskbarCheckpoint? Parse(string json)
    {
        try
        {
            var checkpoint = JsonSerializer.Deserialize<TaskbarCheckpoint>(json, JsonOptions);
            return checkpoint is { SchemaVersion: >= 1 and <= CurrentSchemaVersion } ? checkpoint : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record TaskbarRestorePlan(bool ResetTransparency, bool? SetAutoHide, bool ResetSystemTransparency);

public static class TaskbarRecovery
{
    /// <summary>
    /// Restores only what the app changed. Auto-hide goes back to the recorded value
    /// unless the user has already changed it again themselves; the system
    /// transparency effects go back to off only when the app is the one that turned
    /// them on.
    /// </summary>
    public static TaskbarRestorePlan Plan(TaskbarCheckpoint checkpoint, bool currentAutoHide)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        bool? autoHide = checkpoint.AutoHideApplied
            && currentAutoHide
            && !checkpoint.OriginalAutoHide
                ? false
                : null;
        return new TaskbarRestorePlan(
            checkpoint.TransparencyApplied,
            autoHide,
            checkpoint.OriginalSystemTransparency == false);
    }
}

/// <summary>
/// Where the strip that keeps an auto-hidden bottom taskbar from appearing along the edge
/// goes: two pixels high along the bottom of the monitor, either full-width
/// (<see cref="TaskbarEdgeRevealMode.Disabled"/>) or leaving 15 % of the width
/// (at least 160 DIP) free at each end (<see cref="TaskbarEdgeRevealMode.CornersOnly"/>),
/// where the taskbar still appears.
/// </summary>
public static class TaskbarCornerRules
{
    public const double CornerFraction = 0.15d;
    public const double MinimumCornerDip = 160d;
    public const int StripPixels = 2;
    public const int ExpandedTaskbarThresholdPixels = 8;

    /// <summary>
    /// Computes the blocker strip thickness needed to cover a collapsed auto-hidden taskbar.
    /// Defaults to <see cref="StripPixels"/> (2 px) and expands up to
    /// <see cref="ExpandedTaskbarThresholdPixels"/> when high-DPI or shell builds leave a
    /// slightly taller collapsed taskbar edge exposed above 2 px.
    /// </summary>
    public static int ResolveStripThickness(int collapsedTaskbarVisiblePixels) =>
        Math.Clamp(collapsedTaskbarVisiblePixels, StripPixels, ExpandedTaskbarThresholdPixels);

    public static PixelRect MiddleStrip(PixelRect monitor, uint dpi, int stripPixels = StripPixels)
    {
        var thickness = ResolveStripThickness(stripPixels);
        var corner = (int)Math.Round(Math.Max(monitor.Width * CornerFraction, MinimumCornerDip * dpi / 96d));
        var left = Math.Min(monitor.Left + corner, monitor.Left + monitor.Width / 2);
        var right = Math.Max(monitor.Right - corner, left);
        return new PixelRect(left, monitor.Bottom - thickness, right, monitor.Bottom);
    }

    /// <summary>
    /// Full-width strip along the bottom edge, blocking pointer edge reveal
    /// across the entire monitor width while Win key and Dock Start button remain usable.
    /// </summary>
    public static PixelRect FullEdgeStrip(PixelRect monitor, int stripPixels = StripPixels)
    {
        var thickness = ResolveStripThickness(stripPixels);
        if (monitor.Width <= 0 || monitor.Height < thickness)
        {
            return new PixelRect(monitor.Left, monitor.Bottom, monitor.Left, monitor.Bottom);
        }
        return new PixelRect(monitor.Left, monitor.Bottom - thickness, monitor.Right, monitor.Bottom);
    }

    public static PixelRect? StripForMode(
        PixelRect monitor,
        uint dpi,
        TaskbarEdgeRevealMode mode,
        int stripPixels = StripPixels) => mode switch
    {
        TaskbarEdgeRevealMode.CornersOnly => MiddleStrip(monitor, dpi, stripPixels) is { Width: > 0 } middle ? middle : null,
        TaskbarEdgeRevealMode.Disabled => FullEdgeStrip(monitor, stripPixels) is { Width: > 0 } full ? full : null,
        _ => null,
    };

    /// <summary>
    /// Returns true when the foreground window is a true full-screen game or video surface
    /// that should pause the bottom-edge strips. Maximized windows, normal resizable
    /// desktop windows with standard chrome (unless explicitly topmost), and tool/overlay
    /// windows keep the strips in place. UWP <c>ApplicationFrameWindow</c> hosts report standard
    /// frame styles even in full-screen and are excluded from the standard-chrome filter.
    /// </summary>
    public static bool ShouldSuspendForFullScreen(
        bool coversMonitor,
        bool foregroundIsZoomed,
        bool hasCaptionAndThickFrame,
        bool isApplicationFrameWindow,
        bool isToolOrOverlayWindow,
        bool isTopmost) =>
        DockVacateRules.IsTrueFullScreen(
            coversMonitor,
            foregroundIsZoomed,
            hasStandardWindowChrome: !isApplicationFrameWindow && hasCaptionAndThickFrame,
            isToolOrOverlayWindow: isToolOrOverlayWindow,
            isTopmost: isTopmost);

    /// <summary>
    /// Whether applying taskbar settings must re-run the native taskbar mode adapter.
    /// Changing only <see cref="TaskbarEdgeRevealMode"/> while the requested mode is already
    /// held cleanly should not tear down and re-enable Windows auto-hide.
    /// </summary>
    public static bool NeedsModeReapply(
        TaskbarMode activeMode,
        TaskbarMode requestedMode,
        string? activeProblem) =>
        activeMode != requestedMode || !string.IsNullOrEmpty(activeProblem);

    /// <summary>
    /// Whether a taskbar window rect belongs to the same monitor bottom edge as
    /// <paramref name="stripBounds"/> (used to prevent a collapsed secondary taskbar from
    /// bypassing the expanded-taskbar guard on another monitor).
    /// </summary>
    public static bool IsSameMonitorBottomTaskbar(PixelRect taskbarRect, PixelRect stripBounds) =>
        taskbarRect.Right > stripBounds.Left
        && taskbarRect.Left < stripBounds.Right
        && taskbarRect.Top < stripBounds.Bottom
        && taskbarRect.Bottom >= stripBounds.Bottom;

    /// <summary>
    /// Whether a taskbar window belongs to the bottom edge of <paramref name="monitorBounds"/>
    /// (including secondary-monitor <c>Shell_SecondaryTrayWnd</c> bars that Windows 11 does not
    /// always report via <c>ABM_GETAUTOHIDEBAREX</c>). Requires horizontal bottom-edge geometry
    /// (<c>Width &gt; Height</c> and <c>Top &gt; monitorBounds.Top</c>) so left/right side-docked
    /// auto-hide taskbars and an upper stacked monitor's bottom taskbar are never matched.
    /// </summary>
    public static bool IsBottomTaskbarOnMonitor(PixelRect taskbarRect, PixelRect monitorBounds) =>
        taskbarRect.Width > taskbarRect.Height
        && taskbarRect.Right > monitorBounds.Left
        && taskbarRect.Left < monitorBounds.Right
        && taskbarRect.Top > monitorBounds.Top
        && taskbarRect.Top < monitorBounds.Bottom
        && taskbarRect.Bottom >= monitorBounds.Bottom;

    /// <summary>
    /// Whether the physical window rect of an edge strip drifted away from its expected bounds
    /// (for example after <c>WM_DPICHANGED</c> or display wake) and must be re-placed.
    /// </summary>
    public static bool NeedsGeometryRestore(PixelRect actualBounds, PixelRect expectedBounds) =>
        actualBounds != expectedBounds;

    /// <summary>
    /// Whether an auto-hidden bottom taskbar overlapping <paramref name="stripBounds"/> is
    /// currently expanded/open rather than collapsed to its thin edge strip. While the taskbar
    /// is intentionally open (Start menu, Win key, tray), edge strips must not fight its Z-order
    /// or raise the Dock over it, which can interfere with auto-hide retraction.
    /// </summary>
    public static bool IsBottomTaskbarExpanded(PixelRect taskbarRect, PixelRect stripBounds) =>
        IsSameMonitorBottomTaskbar(taskbarRect, stripBounds)
        && stripBounds.Bottom - taskbarRect.Top > ExpandedTaskbarThresholdPixels;
}

/// <summary>
/// Stops re-applying an appearance after repeated failures so a changed Explorer
/// cannot keep the adapter in a retry loop.
/// </summary>
public sealed class TaskbarFailureBreaker(int threshold = 3)
{
    public int ConsecutiveFailures { get; private set; }

    public bool IsTripped => ConsecutiveFailures >= threshold;

    public void RecordSuccess() => ConsecutiveFailures = 0;

    public bool RecordFailure()
    {
        ConsecutiveFailures++;
        return IsTripped;
    }

    public void Reset() => ConsecutiveFailures = 0;
}
