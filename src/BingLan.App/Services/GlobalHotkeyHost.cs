using System.Windows.Interop;
using BingLan.App.Interop;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

namespace BingLan.App.Services;

/// <summary>
/// Owns a lightweight Win32 message-only window that registers all machine-local global
/// hotkeys independently of whether the top bar or Dock is open.
/// </summary>
internal sealed class GlobalHotkeyHost : IDisposable
{
    private const int BaseHotkeyId = 0x5E41;
    private static readonly nint HwndMessage = new(-3);

    private readonly HwndSource _source;
    private readonly Dictionary<GlobalHotkeyKind, string> _registered = [];
    private GlobalHotkeysState? _lastState;
    private bool _lastLauncherEnabled;
    private bool _lastCleanDesktopEnabled;
    private bool _disposed;

    internal GlobalHotkeyHost()
    {
        var parameters = new HwndSourceParameters("BingLanGlobalHotkeys")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
            Width = 0,
            Height = 0
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    internal event Action<GlobalHotkeyKind>? HotkeyPressed;

    internal nint Handle => _disposed ? 0 : _source.Handle;

    internal bool IsRegistered(GlobalHotkeyKind kind) => _registered.ContainsKey(kind);

    internal string? RegisteredText(GlobalHotkeyKind kind) =>
        _registered.TryGetValue(kind, out var text) ? text : null;

    internal void Sync(GlobalHotkeysState state, bool launcherEnabled, bool cleanDesktopEnabled)
    {
        if (_disposed || _source.Handle == 0)
        {
            return;
        }
        _lastState = state;
        _lastLauncherEnabled = launcherEnabled;
        _lastCleanDesktopEnabled = cleanDesktopEnabled;

        foreach (var kind in GlobalHotkeyRules.AllKinds)
        {
            var enabled = kind switch
            {
                GlobalHotkeyKind.Launcher or GlobalHotkeyKind.LauncherFiles => launcherEnabled,
                GlobalHotkeyKind.ToggleDesktopIcons => cleanDesktopEnabled,
                _ => true
            };
            var wanted = enabled ? GlobalHotkeyRules.Get(state, kind) : string.Empty;
            SyncSingle(kind, wanted);
        }
    }

    /// <summary>
    /// Checks whether a candidate combination is free, distinguishing another BingLan slot
    /// from an external application. Temporarily lifts <paramref name="kind"/>'s own live
    /// registration so re-recording the same key succeeds.
    /// </summary>
    internal GlobalHotkeyProbeResult Probe(GlobalHotkeyKind kind, string candidateText)
    {
        if (_disposed || _source.Handle == 0 || TopBarSearchRules.ParseHotkey(candidateText) is not { } binding)
        {
            return GlobalHotkeyProbeResult.OwnedByOtherProgram;
        }

        if (GlobalHotkeyRules.FindRegisteredConflict(_registered, kind, candidateText) is { } registeredConflict)
        {
            return GlobalHotkeyProbeResult.OwnedByBingLan(registeredConflict);
        }
        if (_lastState is not null
            && GlobalHotkeyRules.FindInternalConflict(_lastState, kind, candidateText) is { } configuredConflict)
        {
            return GlobalHotkeyProbeResult.OwnedByBingLan(configuredConflict);
        }

        var id = HotkeyId(kind);
        var wasRegistered = _registered.TryGetValue(kind, out var previousText);
        if (wasRegistered)
        {
            UnregisterSingle(kind);
        }

        var free = TopBarNativeMethods.RegisterHotKey(
            _source.Handle,
            id,
            binding.Modifiers | TopBarNativeMethods.ModNoRepeat,
            binding.VirtualKey);
        if (free)
        {
            TopBarNativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        if (_lastState is not null)
        {
            Sync(_lastState, _lastLauncherEnabled, _lastCleanDesktopEnabled);
        }
        else if (wasRegistered && !string.IsNullOrEmpty(previousText))
        {
            SyncSingle(kind, previousText);
        }

        return free
            ? GlobalHotkeyProbeResult.Available
            : GlobalHotkeyProbeResult.OwnedByOtherProgram;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        foreach (var kind in _registered.Keys.ToArray())
        {
            UnregisterSingle(kind);
        }
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private void SyncSingle(GlobalHotkeyKind kind, string wanted)
    {
        if (string.IsNullOrWhiteSpace(wanted))
        {
            UnregisterSingle(kind);
            return;
        }
        if (_registered.TryGetValue(kind, out var existing)
            && string.Equals(existing, wanted, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        UnregisterSingle(kind);
        if (TopBarSearchRules.ParseHotkey(wanted) is not { } binding)
        {
            return;
        }
        if (TopBarNativeMethods.RegisterHotKey(
                _source.Handle,
                HotkeyId(kind),
                binding.Modifiers | TopBarNativeMethods.ModNoRepeat,
                binding.VirtualKey))
        {
            _registered[kind] = TopBarSearchRules.FormatHotkey(binding);
        }
    }

    private void UnregisterSingle(GlobalHotkeyKind kind)
    {
        if (_registered.Remove(kind) && _source.Handle != 0)
        {
            TopBarNativeMethods.UnregisterHotKey(_source.Handle, HotkeyId(kind));
        }
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == TopBarNativeMethods.WmHotkey)
        {
            var id = (int)wParam;
            var offset = id - BaseHotkeyId;
            if (offset >= 0 && offset < GlobalHotkeyRules.AllKinds.Count)
            {
                handled = true;
                HotkeyPressed?.Invoke((GlobalHotkeyKind)offset);
            }
        }
        return 0;
    }

    private static int HotkeyId(GlobalHotkeyKind kind) => BaseHotkeyId + (int)kind;
}
