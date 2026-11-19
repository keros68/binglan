using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Dock;

/// <summary>
/// Owns the ice-blue dock window for the app lifetime. The dock is opt-in; a failure
/// while opening or applying it closes the dock (releasing any reserved work area)
/// without affecting desktop widgets.
/// </summary>
internal sealed class DockHost : IDisposable
{
    private readonly DockState _state;
    private readonly Action _stateChanged;
    private readonly Action _openSettings;
    private readonly Action<string> _notify;
    private IceBlueDockWindow? _window;

    internal DockHost(
        DockState state,
        Action stateChanged,
        Action openSettings,
        Action<string> notify)
    {
        _state = state;
        _stateChanged = stateChanged;
        _openSettings = openSettings;
        _notify = notify;
    }

    internal event Action? ShellRecovered;

    internal event Action? ShowDesktopTriggered;

    internal IEnumerable<nint> ActiveDesktopHandles() =>
        _window?.ActiveDesktopHandles() ?? [];

    internal void EnsureAboveDesktop() =>
        _window?.EnsureAboveDesktop();

    internal void Apply()
    {
        if (!_state.IsEnabled)
        {
            Close();
            return;
        }

        try
        {
            if (_window is null)
            {
                Open();
            }
            else
            {
                _window.ApplyState();
                _window.RefreshWindows();
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
            _window?.RefreshWindows();
            _stateChanged();
        }
        return added;
    }

    private void Open()
    {
        var window = new IceBlueDockWindow(_state);
        window.DockStateChanged += _stateChanged;
        window.SettingsRequested += _openSettings;
        window.CommandFailed += _notify;
        window.ShellRecovered += () => ShellRecovered?.Invoke();
        window.ShowDesktopTriggered += () => ShowDesktopTriggered?.Invoke();
        window.HideRequested += () =>
        {
            _state.IsEnabled = false;
            Close();
            _stateChanged();
        };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_window, window))
            {
                _window = null;
            }
        };
        _window = window;
        window.Show();
    }

    private void Close()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }
}
