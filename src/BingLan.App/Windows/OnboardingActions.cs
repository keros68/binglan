using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Themes;

namespace BingLan.App.Windows;

/// <summary>A local app found (or chosen) for one of the guide's starter slots.</summary>
public sealed record OnboardingEntry(string Slot, DockPinnedApp? App);

/// <summary>What the first-run guide may change; the coordinator owns the state.</summary>
/// <summary>One display offered in the first-run guide: resolution and scaling in the label.</summary>
public sealed record OnboardingMonitor(string? MonitorKey, string Label, bool IsPrimary);

public sealed class OnboardingActions
{
    public CityLookupService CitySearch { get; init; } = new();
    /// <summary>Connected displays, primary first; the chosen one hosts the dock.</summary>
    public IReadOnlyList<OnboardingMonitor> Monitors { get; init; } = [];
    public Action<string?> ApplyMonitor { get; init; } = _ => { };
    /// <summary>The folder offered for the Downloads entry, or null when none was found.</summary>
    public string? DownloadsFolder { get; init; }
    public string GreetingName { get; init; } = string.Empty;
    public bool CanChangeStartup { get; init; }
    public Action<DesktopLayoutPreset> ApplyPreset { get; init; } = _ => { };
    public Func<string, ThemeReadResult> ReadTheme { get; init; } =
        _ => new ThemeReadResult(null, "主题导入不可用");
    public Func<string, Task<ThemeImportOutcome>> ImportTheme { get; init; } =
        _ => Task.FromResult(new ThemeImportOutcome(false, string.Empty, []));
    public Action<string> ApplyGreetingName { get; init; } = _ => { };
    public Action<CitySearchResult> ApplyCity { get; init; } = _ => { };
    public Func<Task<IReadOnlyList<OnboardingEntry>>> DetectEntries { get; init; } =
        () => Task.FromResult<IReadOnlyList<OnboardingEntry>>([]);
    /// <summary>Pins the chosen apps and, when a folder is given, adds it as the Downloads entry.</summary>
    public Action<IReadOnlyList<DockPinnedApp>, string?> ApplyEntries { get; init; } = (_, _) => { };
    public Action<DesktopMode> ApplyDesktopMode { get; init; } = _ => { };
    public Action<bool> SetStartupEnabled { get; init; } = _ => { };
    public Action Complete { get; init; } = () => { };
    public Action OpenSettings { get; init; } = () => { };
}
