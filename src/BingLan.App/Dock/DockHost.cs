using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Dock;

/// <summary>
/// Owns the enabled dock surfaces for the app lifetime. Each surface releases its
/// reserved work area when its display is disabled or disconnected.
/// </summary>
internal sealed class DockHost : IDisposable
{
    private readonly DockState _state;
    private readonly DesktopStyleState _style;
    private readonly Action _stateChanged;
    private readonly Action _openSettings;
    private readonly Action<string> _notify;
    private readonly Dictionary<string, IceBlueDockWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly DockRuntime _runtime = new();
    private bool _suppressed;

    internal DockHost(
        DockState state,
        DesktopStyleState style,
        Action stateChanged,
        Action openSettings,
        Action<string> notify)
    {
        _state = state;
        _style = style;
        _stateChanged = stateChanged;
        _openSettings = openSettings;
        _notify = notify;
    }

    internal event Action? ShellRecovered;

    internal event Action? ExplorerNotified;

    internal event Action? ShowDesktopTriggered;

    internal event Action? LauncherRequested;

    internal Func<IReadOnlyList<(Guid Id, string Title)>>? ReadFileBoxes { get; set; }

    internal Func<Guid, string, Task<string>>? SendToFileBox { get; set; }

    internal Func<nint, nint>? ResolvePanelForeground { get; set; }
    internal Func<MonitorSnapshot, int?>? ResolveTopBarReservedBottomPx { get; set; }
    internal Func<Task<bool>>? OpenTrayOverflowRequested { get; set; }
    internal Func<bool> IsLauncherEnabled { get; set; } = () => true;
    internal Func<IReadOnlyDictionary<string, ScreenSurfacePreferences>?>? ReadScreenPreferences { get; set; }
    internal Func<string, ScreenSurfacePreferences?>? GetOrCreateScreenPreferences { get; set; }

    internal IEnumerable<nint> ActiveDesktopHandles() =>
        _windows.Values.SelectMany(window => window.ActiveDesktopHandles());

    internal void EnsureAboveDesktop()
    {
        foreach (var window in _windows.Values.ToArray()) window.EnsureAboveDesktop();
    }

    internal void RefreshAdaptiveAppearance()
    {
        foreach (var window in _windows.Values.ToArray()) window.RefreshAdaptiveAppearance();
    }

    internal bool IsSuppressed => _suppressed;

    internal bool HasAttention => _windows.Values.Any(window => window.HasAttention);

    internal bool IsRunningOnMonitor(string monitorKey) => _windows.ContainsKey(monitorKey);

    internal bool IsVisibleOnMonitor(string monitorKey) =>
        _windows.TryGetValue(monitorKey, out var window) && window.ActiveDesktopHandles().Any();

    internal void SetSuppressed(bool suppressed)
    {
        if (_suppressed == suppressed)
        {
            return;
        }
        _suppressed = suppressed;
        Apply();
    }

    internal void Apply()
    {
        if (_suppressed)
        {
            Close();
            return;
        }

        try
        {
            var preferences = ReadScreenPreferences?.Invoke();
            var targets = SurfaceMonitorTargetResolver.ResolveTargets(
                _state.MonitorKey, _state.IsEnabled,
                preferences, screen => screen.DockEnableMode);
            var activeKeys = targets.Select(target => target.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _windows.Keys.Where(key => !activeKeys.Contains(key)).ToArray())
            {
                Close(key);
            }
            foreach (var target in targets)
            {
                try
                {
                    if (!_windows.TryGetValue(target.Key, out var window))
                    {
                        Open(target, preferences is not null);
                    }
                    else
                    {
                        window.ScreenSelection = preferences is null ? null : target;
                        window.LauncherEnabled = IsLauncherEnabled();
                        window.ApplyState();
                        window.RefreshWindows();
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"Dock 打开失败：{exception}");
                    Close(target.Key);
                    _notify("冰蓝 Dock 未能打开，已恢复屏幕工作区。");
                }
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"Dock 打开失败：{exception}");
            Close();
            _notify("冰蓝 Dock 未能打开，已恢复屏幕工作区。");
        }
    }

    public void Dispose() => Close();

    /// <summary>Pins the apps pinned to the Windows taskbar. Returns how many were added.</summary>
    internal int ImportTaskbarPins()
    {
        var added = 0;
        foreach (var app in DockShortcuts.ReadTaskbarPins())
        {
            if (DockPinRules.Pin(_state, app))
            {
                added++;
            }
        }
        if (added > 0)
        {
            RefreshSharedItems();
            _stateChanged();
        }
        return added;
    }

    private void Open(SurfaceMonitorSelection target, bool useScreenPreferences)
    {
        var window = new IceBlueDockWindow(_state, _style, _runtime)
        {
            ScreenSelection = useScreenPreferences ? target : null,
            GetOrCreateScreenPreferences = () => GetOrCreateScreenPreferences?.Invoke(target.Key),
            ReadFileBoxes = ReadFileBoxes,
            SendToFileBox = SendToFileBox,
            ResolvePanelForeground = ResolvePanelForeground,
            ResolveTopBarReservedBottomPx = ResolveTopBarReservedBottomPx,
            OpenTrayOverflowRequested = OpenTrayOverflowRequested,
            LauncherEnabled = IsLauncherEnabled()
        };
        if (LauncherRequested is not null)
        {
            window.LauncherRequested += LauncherRequested;
        }
        window.DockStateChanged += _stateChanged;
        window.SharedItemsChanged += RefreshSharedItems;
        window.SettingsRequested += _openSettings;
        window.CommandFailed += _notify;
        window.ShellRecovered += () => ShellRecovered?.Invoke();
        window.ExplorerNotified += () => ExplorerNotified?.Invoke();
        window.ShowDesktopTriggered += () => ShowDesktopTriggered?.Invoke();
        window.HideRequested += () =>
        {
            if (window.ScreenSelection is not null)
            {
                if (GetOrCreateScreenPreferences?.Invoke(target.Key) is not { } preferences)
                {
                    return;
                }
                preferences.DockEnableMode = ScreenSurfaceEnableMode.Disabled;
                Close(target.Key);
            }
            else
            {
                _state.IsEnabled = false;
                Apply();
            }
            _stateChanged();
        };
        window.Closed += (_, _) =>
        {
            if (_windows.TryGetValue(target.Key, out var current) && ReferenceEquals(current, window))
            {
                _windows.Remove(target.Key);
            }
        };
        _windows.Add(target.Key, window);
        window.Show();
    }

    private void RefreshSharedItems()
    {
        _runtime.InvalidateVisuals();
        foreach (var window in _windows.Values.ToArray()) window.RefreshSharedItems();
    }

    private void Close(string key)
    {
        if (_windows.Remove(key, out var window)) window.Close();
    }

    private void Close()
    {
        var windows = _windows.Values.ToArray();
        _windows.Clear();
        foreach (var window in windows) window.Close();
    }
}
