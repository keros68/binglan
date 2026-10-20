using BingLan.Core.Dock;

namespace BingLan.Core.Models;

public enum DockVisibilityMode
{
    ReserveWorkArea,
    SmartHide
}

public sealed class DockPinnedApp
{
    public string DisplayName { get; set; } = string.Empty;
    public string? ExecutablePath { get; set; }
    public string? AppUserModelId { get; set; }

    /// <summary>
    /// Command-line arguments of the source shortcut, verbatim, or null when the
    /// program needs none. Packaged apps (app model ID) never carry arguments.
    /// </summary>
    public string? Arguments { get; set; }

    /// <summary>
    /// Folder the shortcut starts the program in, or null to start in the program's
    /// own folder like Explorer does when opening an executable directly.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// File name of a custom icon in the app's icon folder, or null for the app's own
    /// icon. Only a bare generated name is kept, never a path.
    /// </summary>
    public string? IconFile { get; set; }
}

public sealed class DockState
{
    public const double MinimumIconSize = 32d;
    public const double MaximumIconSize = 64d;
    public const double DefaultIconSize = 40d;
    public const double MinimumBottomGapDip = 0d;
    public const double MaximumBottomGapDip = 64d;
    public const double DefaultBottomGapDip = DockLayoutMetrics.EdgeGapDip;
    public const string DefaultSurfaceColor = "#EAF5FC";
    public const double DefaultSurfaceOpacity = 0.84d;

    public bool IsEnabled { get; set; }
    /// <summary>
    /// Whether the fixed Windows Start menu entry appears at the left edge of the Dock.
    /// This is a machine-local preference and is never part of a shared theme.
    /// </summary>
    public bool ShowStartMenuButton { get; set; } = true;
    public string? MonitorDeviceName { get; set; }
    public DockVisibilityMode VisibilityMode { get; set; } = DockVisibilityMode.ReserveWorkArea;
    public List<DockPinnedApp> PinnedApps { get; set; } = [];

    /// <summary>
    /// Apps whose running windows the dock leaves out, for windows that keep themselves
    /// off the Windows taskbar in ways the dock cannot detect.
    /// </summary>
    public List<DockPinnedApp> HiddenApps { get; set; } = [];

    /// <summary>Icon edge length in DIPs; the dock grows with it.</summary>
    public double IconSize { get; set; } = DefaultIconSize;

    /// <summary>Gap between the dock's bottom edge and the work-area bottom, in DIPs.</summary>
    public double BottomGapDip { get; set; } = DefaultBottomGapDip;

    /// <summary>
    /// Whether a reserved dock releases its work-area strip while a window is
    /// maximized on the dock's monitor, letting the window use the full height.
    /// </summary>
    public bool ReleaseWhenMaximized { get; set; }

    /// <summary>
    /// Top-left of the hidden-dock handle in DIPs relative to the dock monitor, or
    /// null to keep the default corner until the user drags it.
    /// </summary>
    public double? HiddenHandleLeftDip { get; set; }

    public double? HiddenHandleTopDip { get; set; }

    /// <summary>Whether the dock takes its colours from the card material.</summary>
    public bool FollowCardLook { get; set; } = true;

    /// <summary>The dock's own colour, used when it does not follow the card material.</summary>
    public string SurfaceColor { get; set; } = DefaultSurfaceColor;

    private double _surfaceOpacity = DefaultSurfaceOpacity;

    /// <summary>
    /// The dock's own glass density when it does not follow the cards, presented with
    /// the same 5–96% range as the shared glass density.
    /// </summary>
    public double SurfaceOpacity
    {
        get => _surfaceOpacity;
        set => _surfaceOpacity = double.IsFinite(value)
            ? Math.Clamp(value, DesktopStyleRules.MinimumGlassDensity, DesktopStyleRules.MaximumGlassDensity)
            : DefaultSurfaceOpacity;
    }
}
