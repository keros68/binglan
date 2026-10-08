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

/// <summary>Startup, data folder, backup and theme actions offered by the settings window.</summary>
public sealed class SettingsMaintenance
{
    public bool CanChangeStartup { get; init; }
    public Func<bool> IsStartupEnabled { get; init; } = () => false;
    public Action<bool> SetStartupEnabled { get; init; } = _ => { };
    public string DataDirectory { get; init; } = string.Empty;
    public Action OpenDataDirectory { get; init; } = () => { };
    public Func<string> CreateBackup { get; init; } = () => string.Empty;
    public Func<IReadOnlyList<string>> ListBackups { get; init; } = () => [];
    public Action<string> RestoreBackup { get; init; } = _ => { };
    public Func<WidgetWindowBase?, BingLan.Core.Models.DesktopComponentKind?, bool> ResetWidgetPlacement { get; init; } =
        (_, _) => false;

    /// <summary>The icons in the quick-launch row, and saving a changed list.</summary>
    public Func<IReadOnlyList<BingLan.Core.Models.QuickPlaceState>> QuickPlaces { get; init; } = () => [];
    public Action<List<BingLan.Core.Models.QuickPlaceState>> SetQuickPlaces { get; init; } = _ => { };

    /// <summary>Applies a look read from a Rainmeter skin to every card; returns a status line.</summary>
    public Func<RainmeterLook, string, string> ImportRainmeterLook { get; init; } = (_, _) => string.Empty;

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
    /// <summary>Creates a card of the given kind and returns its window, or null.</summary>
    public Func<NewWidgetKind, WidgetWindowBase?> AddWidget { get; init; } = _ => null;
    public Action ExitApp { get; init; } = () => { };

    /// <summary>Update checking and installing, or null where it is not offered.</summary>
    public BingLan.App.Services.AppUpdater? Updater { get; init; }

    public Func<int> ImportTaskbarPins { get; init; } = () => 0;

    /// <summary>Writes the shared card material from <see cref="Style"/> to every card.</summary>
    public Action ApplyGlass { get; init; } = () => { };
    public Func<bool> IsCleanDesktopEnabled { get; init; } = () => false;
    public Func<bool, Task<string>> SetCleanDesktop { get; init; } =
        _ => Task.FromResult("清爽桌面不可用");
    public Func<string> CleanDesktopNotice { get; init; } = () => string.Empty;
    public Func<Task<string>> RetryCleanDesktopRestore { get; init; } = () => Task.FromResult(string.Empty);

    /// <summary>File-box automation (auto-collect new desktop items, auto-clear gone mappings).</summary>
    public Func<bool> IsFileBoxAutomationEnabled { get; init; } = () => false;
    public Action<bool> SetFileBoxAutomation { get; init; } = _ => { };
}
