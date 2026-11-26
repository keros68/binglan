using BingLan.Core.Themes;

namespace BingLan.App.Windows;

/// <summary>One theme dock slot the importing machine could not resolve on its own.</summary>
public sealed record ThemeMissingSlot(ThemeAppBinding Binding, int OriginalIndex);

/// <summary>Result of applying an imported theme: whether it was applied, a status
/// message and any dock slots left for the binding wizard to resolve.</summary>
public sealed record ThemeImportOutcome(bool Succeeded, string Message, IReadOnlyList<ThemeMissingSlot> MissingSlots);

public enum NewWidgetKind
{
    Todo,
    Note,
    FileBox
}

/// <summary>Result of binding one missing theme slot to a local app.</summary>
public sealed record BindOutcome(bool Succeeded, string Message);

/// <summary>
/// Summary of one connected or remembered monitor shown in the multi-screen topology switcher.
/// </summary>
public sealed record MonitorCardOption(
    string? ScreenKey,
    string MonitorKey,
    string DisplayLabel,
    string ShortLabel,
    string ResolutionSummary,
    int DisplayIndex,
    bool IsPrimary,
    bool IsConnected,
    BingLan.Core.Dock.PixelRect Bounds,
    BingLan.Core.Dock.PixelRect WorkingArea,
    uint Dpi,
    int CardCount);

/// <summary>Result of moving a desktop card to another screen.</summary>
public sealed record MoveWidgetResult(
    bool Succeeded,
    string Message,
    WidgetWindowBase? ResultingWindow);

/// <summary>
/// Bridge between <see cref="SettingsWindow"/> and <see cref="WidgetCoordinator"/> for
/// multi-screen card profile management. Null in single-screen tests that do not attach it.
/// </summary>
public sealed class MultiScreenSettingsBridge
{
    public Func<bool> IsEnabled { get; init; } = () => false;
    public Func<bool> HasMultipleMonitors { get; init; } = () => false;
    public Func<int> GetConnectedMonitorCount { get; init; } = () => 1;
    public Func<bool> HasSavedSecondaryState { get; init; } = () => false;
    public Action<bool> SetEnabled { get; init; } = _ => { };
    public Func<IReadOnlyList<MonitorCardOption>> GetScreens { get; init; } = () => [];
    public Func<string?, BingLan.Core.Models.DesktopExperienceState> GetExperience { get; init; } =
        _ => BingLan.Core.Models.DesktopExperienceRules.CreateDefault();
    public Action<string?, BingLan.Core.Models.DesktopExperienceState> ApplyExperience { get; init; } = (_, _) => { };
    public Action<string?, BingLan.Core.Models.DesktopExperienceState> ApplyLayoutPreset { get; init; } = (_, _) => { };
    public Func<string?, IReadOnlyList<BingLan.Core.Models.QuickPlaceState>> GetQuickPlaces { get; init; } = _ => [];
    public Action<string?, List<BingLan.Core.Models.QuickPlaceState>> SetQuickPlaces { get; init; } = (_, _) => { };
    public Func<string?, Guid?> GetPrimaryTodoId { get; init; } = _ => null;
    public Func<string?, IReadOnlyList<WidgetWindowBase>> GetWindowsForScreen { get; init; } = _ => [];
    public Func<WidgetWindowBase, string?> GetScreenKeyForWindow { get; init; } = _ => null;
    public Func<string?, NewWidgetKind, WidgetWindowBase?> AddWidgetOnScreen { get; init; } = (_, _) => null;
    public Func<string?, WidgetWindowBase?, BingLan.Core.Models.DesktopComponentKind?, bool> ResetWidgetPlacement { get; init; } =
        (_, _, _) => false;
    public Func<string, string> CopyPrimaryProfileToScreen { get; init; } = _ => string.Empty;
    public Func<WidgetWindowBase, string?, MoveWidgetResult> MoveWidgetToScreen { get; init; } =
        (_, _) => new MoveWidgetResult(false, string.Empty, null);
    public Func<IReadOnlyList<BingLan.Core.Models.DisconnectedScreenWidget>> GetDisconnectedWidgets { get; init; } =
        () => [];
    public Func<Guid, string, bool> MoveDisconnectedWidgetToPrimary { get; init; } = (_, _) => false;
    public Func<string, int> MoveAllDisconnectedFromScreenToPrimary { get; init; } = _ => 0;
}

/// <summary>Startup, data folder, backup and theme actions offered by the settings window.</summary>
public sealed class SettingsMaintenance
{
    public bool CanChangeStartup { get; init; }
    public Func<bool> IsStartupEnabled { get; init; } = () => false;
    public Action<bool> SetStartupEnabled { get; init; } = _ => { };
    public Func<Guid?> PrimaryTodoId { get; init; } = () => null;
    public string DataDirectory { get; init; } = string.Empty;
    public Action OpenDataDirectory { get; init; } = () => { };
    public Func<string> CreateBackup { get; init; } = () => string.Empty;
    public Func<IReadOnlyList<string>> ListBackups { get; init; } = () => [];
    public Func<string, bool> DeleteBackup { get; init; } = _ => false;
    public Action<string> RestoreBackup { get; init; } = _ => { };
    public Func<WidgetWindowBase?, BingLan.Core.Models.DesktopComponentKind?, bool> ResetWidgetPlacement { get; init; } =
        (_, _) => false;

    /// <summary>The icons in the quick-launch row, and saving a changed list.</summary>
    public Func<IReadOnlyList<BingLan.Core.Models.QuickPlaceState>> QuickPlaces { get; init; } = () => [];
    public Action<List<BingLan.Core.Models.QuickPlaceState>> SetQuickPlaces { get; init; } = _ => { };

    /// <summary>Applies a look read from a Rainmeter skin to every card; returns a status line.</summary>
    public Func<RainmeterLook, string, string> ImportRainmeterLook { get; init; } = (_, _) => string.Empty;

    /// <summary>Shows or hides a standalone widget (note or file box) without deleting it.</summary>
    public Action<WidgetWindowBase, bool>? SetWidgetVisibility { get; init; }

    /// <summary>Toggles left/right outer screen edge docking for a file box.</summary>
    public Func<FileBoxWindow, bool, bool>? ToggleFileBoxEdgeDock { get; init; }

    /// <summary>Merges source file box (or group) into target as tabs; returns the combined host.</summary>
    public Func<FileBoxWindow, FileBoxWindow, FileBoxWindow?>? MergeFileBoxes { get; init; }

    /// <summary>Detaches a single tab out of a grouped file box into a standalone window.</summary>
    public Func<FileBoxWindow, BingLan.Core.Models.FileBoxState, FileBoxWindow?>? DetachFileBoxTab { get; init; }

    /// <summary>Splits all tabs of a grouped file box back into standalone windows.</summary>
    public Action<FileBoxWindow>? SplitFileBoxGroup { get; init; }

    /// <summary>Deletes (or, for built-in cards, hides) a card after asking; true when it went.</summary>
    public Func<WidgetWindowBase, bool> DeleteWidget { get; init; } = _ => false;
    public Func<string, string, string> ExportTheme { get; init; } = (_, _) => string.Empty;
    public Func<string, ThemeReadResult> ReadTheme { get; init; } =
        _ => new ThemeReadResult(null, "主题导入不可用");
    public Func<string, Task<ThemeImportOutcome>> ImportTheme { get; init; } =
        _ => Task.FromResult(new ThemeImportOutcome(false, string.Empty, []));
    public Func<ThemeAppBinding, string, int, BindOutcome> PinBoundApp { get; init; } =
        (_, _, _) => new BindOutcome(false, string.Empty);
    public BingLan.Core.Models.DesktopStyleState Style { get; init; } = new();
    public Action ApplyStyle { get; init; } = () => { };
    public BingLan.Core.Models.LauncherAppearanceState LauncherAppearance { get; init; } = new();
    public Action ApplyLauncherAppearance { get; init; } = () => { };
    public Func<bool> IsLauncherEnabled { get; init; } = () => true;
    public Action<bool> SetLauncherEnabled { get; init; } = _ => { };
    public Func<bool> IsLauncherShowRecentFiles { get; init; } = () => true;
    public Action<bool> SetLauncherShowRecentFiles { get; init; } = _ => { };
    public Func<bool> IsLauncherUseEverything { get; init; } = () => false;
    public Action<bool> SetLauncherUseEverything { get; init; } = _ => { };
    public Func<BingLan.Core.Models.LauncherFavoriteMode> GetLauncherFavoriteMode { get; init; } =
        () => BingLan.Core.Models.LauncherFavoriteMode.FollowDock;
    public Action<BingLan.Core.Models.LauncherFavoriteMode> SetLauncherFavoriteMode { get; init; } = _ => { };
    public Func<BingLan.Core.Models.LauncherBackdropMode> GetLauncherBackdropMode { get; init; } =
        () => BingLan.Core.Models.LauncherBackdropMode.LiveWindow;
    public Action<BingLan.Core.Models.LauncherBackdropMode> SetLauncherBackdropMode { get; init; } = _ => { };
    /// <summary>Creates a card of the given kind and returns its window, or null.</summary>
    public Func<NewWidgetKind, WidgetWindowBase?> AddWidget { get; init; } = _ => null;
    public Action ExitApp { get; init; } = () => { };

    /// <summary>Whether this copy was installed by setup and can be uninstalled.</summary>
    public bool CanUninstall { get; init; }

    /// <summary>Starts the uninstaller and exits; returns why it could not start, or null.</summary>
    public Func<string?> Uninstall { get; init; } = () => null;

    /// <summary>Update checking and installing, or null where it is not offered.</summary>
    public BingLan.App.Services.AppUpdater? Updater { get; init; }

    public Func<int> ImportTaskbarPins { get; init; } = () => 0;

    /// <summary>Writes the shared card material from <see cref="Style"/> to every card.</summary>
    public Action ApplyGlass { get; init; } = () => { };
    public Func<bool> IsCleanDesktopEnabled { get; init; } = () => false;
    public Func<bool> IsCleanDesktopAvailable { get; init; } = () => true;
    public Func<bool, Task<string>> SetCleanDesktop { get; init; } =
        _ => Task.FromResult("当前无法隐藏 Windows 桌面图标");
    public Func<bool> IsDoubleClickDesktopToToggleIconsEnabled { get; init; } = () => false;
    public Func<bool, string> SetDoubleClickDesktopToToggleIcons { get; init; } = _ => string.Empty;
    public Func<string> CleanDesktopNotice { get; init; } = () => string.Empty;

    /// <summary>
    /// Whether the desktop icons are hidden while the mode is off — an orphaned state
    /// the settings page offers to restore. The Shell query runs off the UI thread.
    /// </summary>
    public Func<Task<bool>> CleanDesktopIconsOrphaned { get; init; } = () => Task.FromResult(false);

    /// <summary>Shows the desktop icons again from inside the app; returns a status line.</summary>
    public Func<Task<string>> RestoreCleanDesktopIcons { get; init; } = () => Task.FromResult(string.Empty);

    /// <summary>File-box automation (auto-collect new desktop items, auto-clear gone mappings).</summary>
    public Func<bool> IsFileBoxAutomationEnabled { get; init; } = () => false;
    public Action<bool> SetFileBoxAutomation { get; init; } = _ => { };
    public Func<bool> IsFileBoxDeleteOriginalsEnabled { get; init; } = () => false;
    public Action<bool> SetFileBoxDeleteOriginals { get; init; } = _ => { };
    public Func<bool> IsHeaderToolsOnHover { get; init; } = () => false;
    public Action<bool> SetHeaderToolsOnHover { get; init; } = _ => { };
    public BingLan.Core.Models.GlobalHotkeysState Hotkeys { get; init; } = new();
    public Func<BingLan.Core.Models.GlobalHotkeyKind, string, BingLan.Core.Models.GlobalHotkeyProbeResult> ProbeGlobalHotkey { get; init; } =
        (_, _) => BingLan.Core.Models.GlobalHotkeyProbeResult.Available;
    public Action ApplyGlobalHotkeys { get; init; } = () => { };
    public Func<string?>? GetLastSettingsPage { get; init; }
    public Action<string>? SetLastSettingsPage { get; init; }
    public MultiScreenSettingsBridge? MultiScreen { get; init; }
}
