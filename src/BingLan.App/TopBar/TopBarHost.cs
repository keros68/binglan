using BingLan.App.Interop;
using BingLan.App.Dock;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;
using Microsoft.Win32;
using System.Windows.Threading;

namespace BingLan.App.TopBar;

/// <summary>
/// Owns the top bar windows for the app lifetime, mirroring the dock host. The bar is
/// opt-in; a failure while opening or applying it closes the bar (releasing any reserved
/// work area) without affecting the desktop widgets.
/// </summary>
internal sealed class TopBarHost : IDisposable
{
    private readonly TopBarState _state;
    private readonly DesktopStyleState _style;
    private readonly ITopBarEnvironment _environment;
    private readonly IPerformanceSamplingService _sampler;
    private readonly Action<string> _notify;
    private TopBarWindow? _window;
    private readonly Dictionary<string, TopBarWindow> _windows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _applying;
    private bool _disposed;
    private bool _desktopShown;
    private bool _desktopTopmost;
    private bool _suppressed;

    internal TopBarHost(
        TopBarState state,
        DesktopStyleState style,
        ITopBarEnvironment environment,
        IPerformanceSamplingService sampler,
        Action<string> notify)
    {
        _state = state;
        _style = style;
        _environment = environment;
        _sampler = sampler;
        _notify = notify;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    internal event Action? ShellRecovered;

    internal event Action? ExplorerNotified;

    internal Func<IReadOnlyDictionary<string, ScreenSurfacePreferences>?>? ReadScreenPreferences { get; set; }

    internal Func<MonitorSnapshot, int?>? ResolveBottomDockTop { get; set; }

    internal void RefreshPeripheralBattery()
    {
        foreach (var window in _windows.Values.ToArray()) window.RenderPeripheralModule();
    }

    internal bool HasWindow => _windows.Count > 0;

    internal bool IsRunningOnMonitor(string monitorKey) => _windows.ContainsKey(monitorKey);

    internal bool IsVisibleOnMonitor(string monitorKey) =>
        _windows.TryGetValue(monitorKey, out var window) && window.ActiveDesktopHandles().Any();

    internal void RefreshTodoSummary()
    {
        foreach (var window in _windows.Values.ToArray()) window.RefreshTodoSummary();
    }

    internal void RefreshClock()
    {
        foreach (var window in _windows.Values.ToArray()) window.RefreshClock();
    }

    internal void RefreshAppearance()
    {
        foreach (var window in _windows.Values.ToArray()) window.RefreshAppearance();
    }

    internal IEnumerable<nint> ActiveDesktopHandles() =>
        _windows.Values.SelectMany(window => window.ActiveDesktopHandles()).ToArray();

    internal int? GetReservedBottomForMonitor(MonitorSnapshot monitor) =>
        _windows.Values.Select(window => window.GetReservedBottomForMonitor(monitor))
            .Where(bottom => bottom.HasValue).Max();

    internal void SetDesktopShown(bool shown, nint cardSlot, bool topmost)
    {
        _desktopShown = shown;
        _desktopTopmost = topmost;
        foreach (var window in _windows.Values.ToArray()) window.SetDesktopShown(shown, cardSlot, topmost);
    }

    internal bool IsSuppressed => _suppressed;

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
        if (_disposed || _applying) return;
        if (_suppressed)
        {
            Close();
            return;
        }

        _applying = true;
        try
        {
            var targets = SurfaceMonitorTargetResolver.ResolveTargets(
                _state.MonitorKey, _state.IsEnabled,
                ReadScreenPreferences?.Invoke(), preferences => preferences.TopBarEnableMode);
            var desired = targets.Select(target => target.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _windows.Keys.Where(key => !desired.Contains(key)).ToArray())
            {
                var obsolete = _windows[key];
                _windows.Remove(key);
                obsolete.Close();
            }
            foreach (var target in targets)
            {
                try
                {
                    if (_windows.TryGetValue(target.Key, out var window))
                    {
                        window.ApplyScreenTarget(target);
                    }
                    else
                    {
                        Open(target);
                    }
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"顶端信息条打开失败：{exception}");
                    if (_windows.Remove(target.Key, out var failed)) failed.Close();
                    _notify("顶部信息条未能打开，已恢复屏幕工作区。");
                }
            }
            var baseline = SurfaceMonitorTargetResolver.Resolve(_state.MonitorKey);
            var liveTargets = targets.Where(target => _windows.ContainsKey(target.Key)).ToArray();
            var ownerKey = liveTargets.FirstOrDefault(target => target.Monitor.MonitorHandle == baseline?.MonitorHandle)?.Key
                ?? liveTargets.FirstOrDefault(target => target.Monitor.IsPrimary)?.Key
                ?? liveTargets.OrderBy(target => target.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Key;
            var nextOwner = ownerKey is not null ? _windows.GetValueOrDefault(ownerKey) : null;
            if (!ReferenceEquals(_window, nextOwner))
            {
                _window?.SetGlobalEventOwner(false);
                _window?.CloseSearchPopup();
                _window = nextOwner;
                _window?.SetGlobalEventOwner(true);
            }
            if (_desktopShown)
            {
                SetDesktopShown(true, _desktopTopmost ? DockNativeMethods.HwndTopmost : DockNativeMethods.HwndTop,
                    _desktopTopmost);
            }
        }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"顶端信息条打开失败：{exception}");
            Close();
            _notify("顶部信息条未能打开，已恢复屏幕工作区。");
        }
        finally
        {
            _applying = false;
            if (_windows.Count == 0) _environment.StopPeripheralBattery();
        }
    }

    /// <summary>
    /// Whether a hotkey text is free to register right now; without a live bar there is
    /// nothing to register on, so the answer is no.
    /// </summary>
    internal bool ProbeSearchHotkey(string text) => _window?.ProbeSearchHotkey(text) ?? false;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Close();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs args) =>
        _dispatcher.BeginInvoke(Apply, DispatcherPriority.Background);

    private void Open(SurfaceMonitorSelection target)
    {
        var window = new TopBarWindow(_state, _style, _environment, _sampler,
            screenTarget: target, ownsGlobalEvents: false)
        {
            ResolveBottomDockTop = ResolveBottomDockTop
        };
        window.SearchRequested = () =>
        {
            foreach (var peer in _windows.Values)
            {
                if (!ReferenceEquals(peer, window)) peer.CloseSearchPopup();
            }
            window.TriggerOwnedSearch();
        };
        window.SystemFactsChanged += facts =>
        {
            foreach (var peer in _windows.Values.ToArray())
            {
                if (!ReferenceEquals(peer, window)) peer.ApplySharedSystemFacts(facts);
            }
        };
        window.InputMethodChanged += () =>
        {
            foreach (var peer in _windows.Values.ToArray())
            {
                if (!ReferenceEquals(peer, window)) peer.RefreshInputMethod();
            }
        };
        window.ShellEventReceived += (shellEvent, handle) =>
        {
            foreach (var peer in _windows.Values.ToArray())
            {
                if (!ReferenceEquals(peer, window)) peer.HandleShellEvent(shellEvent, handle);
            }
        };
        window.EnvironmentChanged += Apply;
        window.ShellRecovered += () => ShellRecovered?.Invoke();
        window.ExplorerNotified += () => ExplorerNotified?.Invoke();
        window.Closed += (_, _) =>
        {
            if (_windows.TryGetValue(target.Key, out var current) && ReferenceEquals(current, window))
            {
                _windows.Remove(target.Key);
            }
        };
        _windows[target.Key] = window;
        window.Show();
    }

    private void Close()
    {
        var windows = _windows.Values.ToArray();
        _windows.Clear();
        _window = null;
        _environment.StopPeripheralBattery();
        foreach (var window in windows) window.Close();
    }
}
