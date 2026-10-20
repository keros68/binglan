using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using BingLan.App.Interop;

namespace BingLan.App.Taskbar;

/// <summary>Opens the Windows-owned overflow panel without changing taskbar settings.</summary>
internal sealed class TrayOverflowController : IDisposable
{
    private const uint GaRoot = 2;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const string PanelClass = "TopLevelWindowForOverflowXamlIsland";
    // Ownership stays armed during the provider call and for this long after it
    // returns. Later Explorer animations require more build-specific validation.
    private const long ClaimWindowMs = 2000;
    private static readonly TimeSpan UiaWait = TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _closeCheck;
    private readonly DispatcherTimer _focusReturn;
    private readonly DockNativeMethods.WinEventProc _panelEventCallback;
    private readonly int _slowUiaMs;
    private readonly Action<string> _log;
    private Task<string>? _uiaCall;
    private nint _panelEvents;
    private uint _explorerProcessId;
    private nint _panelWindow;
    private long _claimUntil;
    private nint _previousForeground;
    private int _opening;
    private bool _ownsPanel;
    private bool _stopping;
    private bool _disposed;

    /// <param name="slowUiaMs">Test-only delay before each UIA toggle, to exercise late cancellation and hangs.</param>
    internal TrayOverflowController(int slowUiaMs = 0, Action<string>? log = null)
    {
        _slowUiaMs = slowUiaMs;
        _log = log ?? (message => System.Diagnostics.Debug.WriteLine(message));
        // Explorer show/hide events end a panel session; the 150 ms poll stays as a
        // fallback until the event path is verified on more builds.
        _closeCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _closeCheck.Tick += (_, _) => { if (!IsVisible) EndSession("poll"); };
        // A tray icon's context menu or window takes focus shortly after the panel
        // hides; only return focus if it is still inside the taskbar by then.
        _focusReturn = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _focusReturn.Tick += (_, _) => RestoreForeground();
        _panelEventCallback = OnPanelEvent;
    }

    internal static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    internal static bool IsVisible
    {
        get
        {
            var window = TaskbarNativeMethods.FindWindowW(PanelClass, null);
            return window != 0 && DockNativeMethods.IsWindowVisible(window);
        }
    }

    internal bool IsStopping => _stopping;

    /// <summary>True while a UIA toggle call has been issued and not yet returned.</summary>
    internal bool IsInvoking => _uiaCall is { IsCompleted: false };

    internal async Task<string> OpenAsync(bool invokeButton = true)
    {
        if (_disposed || _stopping) return "disposed";
        if (IsInvoking) return "pending";
        if (Interlocked.CompareExchange(ref _opening, 1, 0) != 0) return "busy";
        var opened = false;
        var inputSent = false;
        var cancellationToken = _lifetime.Token;
        try
        {
            // Verified on Windows 11 only; older shells keep their own tray behavior.
            if (!IsSupported) return "unsupported-build";
            if (IsVisible) return "already-open";
            var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == 0) return "taskbar-missing";
            WatchPanel(tray);
            _closeCheck.Stop();
            _focusReturn.Stop();
            _previousForeground = DockNativeMethods.GetForegroundWindow();
            if (!ShellSurfaceNativeMethods.SendChord(0x5B, 0x42)) return "rejected";
            inputSent = true;
            if (!invokeButton) return "injected-failure";
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                if (!IsTaskbarForeground())
                {
                    var foreground = DockNativeMethods.GetForegroundWindow();
                    // Foreground can read 0 for a moment while activation moves.
                    if (foreground == _previousForeground || foreground == 0) continue;
                    return "focus-changed";
                }
                var invoked = await InvokeAsync(expectVisible: false, cancellationToken);
                if (invoked is "missing" or "unavailable") continue;
                if (invoked == "state-changed") return "already-open";
                if (invoked != "invoked") return invoked;
                _ownsPanel = true;
                for (var check = 0; check < 10; check++)
                {
                    // Settle the accepted toggle even during cancellation;
                    // CloseAsync must observe the resulting native surface.
                    await Task.Delay(100);
                    if (!IsVisible) continue;
                    opened = true;
                    if (!_stopping) _closeCheck.Start();
                    cancellationToken.ThrowIfCancellationRequested();
                    return "opened";
                }
                return "panel-missing";
            }
            return "button-missing";
        }
        catch (OperationCanceledException) { return "cancelled"; }
        finally
        {
            if (!opened && !IsVisible)
            {
                // A later panel is still claimed by the show event; a user-opened one is not ours.
                _ownsPanel = false;
                if (inputSent) await RestoreAfterInputAsync();
                else RestoreForeground();
            }
            Volatile.Write(ref _opening, 0);
        }
    }

    private bool Claimed
    {
        get
        {
            var deadline = Interlocked.Read(ref _claimUntil);
            return deadline != 0 && (IsInvoking || Environment.TickCount64 <= deadline);
        }
    }

    /// <summary>
    /// Runs at most one UIA toggle at a time. Invoke cannot be cancelled once issued,
    /// so cancellation and the expected panel state are re-checked just before it;
    /// the remaining race is the provider call itself. A call that outlives the wait
    /// is reported as "pending" and still blocks the next toggle.
    /// </summary>
    private async Task<string> InvokeAsync(bool expectVisible, CancellationToken cancellationToken)
    {
        // All callers are on the owner dispatcher. Do not await an older call here:
        // multiple waiters could otherwise resume and each issue a new toggle.
        _closeCheck.Dispatcher.VerifyAccess();
        if (IsInvoking) return "pending";
        var call = Task.Run(() => InvokeButton(expectVisible, cancellationToken));
        _uiaCall = call;
        return await Task.WhenAny(call, Task.Delay(UiaWait)) == call ? await call : "pending";
    }

    private string InvokeButton(bool expectVisible, CancellationToken cancellationToken)
    {
        try
        {
            if (_slowUiaMs > 0) Thread.Sleep(_slowUiaMs);
            var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            if (tray is null) return "missing";
            foreach (AutomationElement button in tray.FindAll(TreeScope.Descendants,
                         new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon")))
            {
                var current = button.Current;
                // These two localized names are verified candidates only. Unknown
                // languages/structures must fail without invoking another tray icon.
                if (current.ClassName == "SystemTray.NormalButton"
                    && current.Name is "显示隐藏的图标" or "Show hidden icons"
                    && current.IsEnabled
                    && button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                    && pattern is InvokePattern invoke)
                {
                    if (cancellationToken.IsCancellationRequested) return "cancelled";
                    // The panel may have closed or opened on its own during the search;
                    // a toggle now would reverse the user's or Explorer's change.
                    if (IsVisible != expectVisible) return "state-changed";
                    if (!expectVisible) Interlocked.Exchange(ref _claimUntil, Environment.TickCount64 + ClaimWindowMs);
                    _log($"OVERFLOW invoke expect-visible={expectVisible}");
                    invoke.Invoke();
                    if (!expectVisible) Interlocked.Exchange(ref _claimUntil, Environment.TickCount64 + ClaimWindowMs);
                    return "invoked";
                }
            }
            return "missing";
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
            or ElementNotEnabledException or InvalidOperationException or COMException or TimeoutException)
        {
            _log($"OVERFLOW UIA unavailable={exception.GetType().Name}");
            return "unavailable";
        }
    }

    /// <summary>Close path used by the cycle check; same gates as exit cleanup.</summary>
    internal async Task<bool> CloseOwnedPanelAsync() =>
        await InvokeAsync(expectVisible: true, CancellationToken.None) == "invoked";

    private void WatchPanel(nint tray)
    {
        // Explorer restarts with a new process; re-hook when it changed.
        DockNativeMethods.GetWindowThreadProcessId(tray, out var processId);
        if (processId == _explorerProcessId && _panelEvents != 0) return;
        if (_panelEvents != 0) DockNativeMethods.UnhookWinEvent(_panelEvents);
        _panelWindow = 0;
        _explorerProcessId = processId;
        _panelEvents = DockNativeMethods.SetWinEventHook(EventObjectDestroy, DockNativeMethods.EventObjectHide, 0,
            _panelEventCallback, processId, 0, DockNativeMethods.WineventOutOfContext);
        _log($"OVERFLOW events hooked={_panelEvents != 0} explorer={processId}");
    }

    private void OnPanelEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || objectId != DockNativeMethods.ObjidWindow || childId != 0 || hwnd == 0) return;
        if (eventType == EventObjectShow)
        {
            var name = new System.Text.StringBuilder(64);
            DockNativeMethods.GetClassNameW(hwnd, name, name.Capacity);
            if (name.ToString() != PanelClass) return;
            _panelWindow = hwnd;
            if (!_ownsPanel && Claimed)
            {
                // A panel that appears after a slow or timed-out invoke is still ours.
                _ownsPanel = true;
                _log("OVERFLOW session-start source=event");
                if (!_stopping) _closeCheck.Start();
            }
        }
        else if (hwnd == _panelWindow)
        {
            // The class cannot be read after destroy, so match the shown handle.
            if (eventType == EventObjectDestroy) _panelWindow = 0;
            EndSession("event");
        }
    }

    private void EndSession(string source)
    {
        _closeCheck.Stop();
        if (!_ownsPanel) return;
        _ownsPanel = false;
        Interlocked.Exchange(ref _claimUntil, 0);
        _log($"OVERFLOW session-end source={source}");
        if (_stopping) RestoreForeground();
        else _focusReturn.Start();
    }

    private static bool IsTaskbarForeground()
    {
        var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
        return BelongsTo(DockNativeMethods.GetForegroundWindow(), tray);
    }

    // Explorer can focus its Composition bridge child instead of the top-level
    // taskbar/overflow HWND. GA_ROOT preserves this ownership boundary.
    private static bool BelongsTo(nint window, nint surface) => surface != 0
        && (window == surface || DockNativeMethods.GetAncestor(window, GaRoot) == surface);

    private void RestoreForeground()
    {
        _focusReturn.Stop();
        var previous = _previousForeground;
        _previousForeground = 0;
        // A user who has switched to another app, or opened a tray icon's menu,
        // keeps that focus. A destroyed original window is not re-activated.
        if (previous == 0 || !DockNativeMethods.IsWindow(previous)) return;
        var foreground = DockNativeMethods.GetForegroundWindow();
        var panel = TaskbarNativeMethods.FindWindowW(PanelClass, null);
        var shellForeground = IsTaskbarForeground() || BelongsTo(foreground, panel);
        if (!_disposed && Claimed && IsInvoking && (foreground == previous || shellForeground))
            _previousForeground = previous;
        if (shellForeground)
        {
            _log($"OVERFLOW restore-focus={DockNativeMethods.SetForegroundWindow(previous)}");
        }
    }

    private async Task RestoreAfterInputAsync()
    {
        // SendInput acceptance precedes Explorer focus changes. Keep the original
        // target for up to 500 ms; a user focus change still takes priority.
        for (var check = 0; check < 10 && _previousForeground != 0; check++)
        {
            if (IsTaskbarForeground()) break;
            var foreground = DockNativeMethods.GetForegroundWindow();
            if (foreground != 0 && foreground != _previousForeground)
            {
                _log($"OVERFLOW preserve-focus previous={_previousForeground} current={foreground}");
                LogWindow(foreground);
                break;
            }
            await Task.Delay(50);
        }
        RestoreForeground();
    }

    private void LogWindow(nint window)
    {
        var name = new System.Text.StringBuilder(256);
        DockNativeMethods.GetClassNameW(window, name, name.Capacity);
        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        DockNativeMethods.GetWindowRect(window, out var rect);
        _log($"OVERFLOW foreground class={name} pid={processId} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} root={DockNativeMethods.GetAncestor(window, GaRoot)}");
    }

    /// <summary>
    /// Cancels pending work and closes the panel this controller opened. Returns
    /// "closed", or why cleanup is not finished: "open-pending"/"close-pending" when a
    /// UIA call has not returned (it cannot be aborted), "close-unconfirmed" or a UIA result.
    /// </summary>
    internal async Task<string> CloseAsync()
    {
        _stopping = true;
        _focusReturn.Stop();
        _lifetime.Cancel();
        // Keep the host HWND alive until pending input and UIA work have settled.
        for (var check = 0; check < 60 && Volatile.Read(ref _opening) != 0; check++)
            await Task.Delay(50);
        if (Volatile.Read(ref _opening) != 0) return "open-pending";
        // An open request may have given up on a slow call that has not reached its
        // cancellation gate yet; its result decides whether a panel can still appear.
        if (_uiaCall is { IsCompleted: false } call && await Task.WhenAny(call, Task.Delay(UiaWait)) != call)
            return "open-pending";

        if (_ownsPanel || Claimed)
        {
            // A late panel from an invoke that just returned can still appear.
            for (var check = 0; check < ClaimWindowMs / 100 && !IsVisible && Claimed; check++) await Task.Delay(100);
            if (IsVisible)
            {
                // Focus restoration alone does not collapse this XAML surface on the
                // verified build. Use the same native toggle that closes it normally.
                var result = await InvokeAsync(expectVisible: true, CancellationToken.None);
                if (result == "pending") return "close-pending";
                if (result is not ("invoked" or "state-changed")) return result;
                for (var check = 0; check < 20 && IsVisible; check++) await Task.Delay(50);
                if (IsVisible) return "close-unconfirmed";
            }
        }
        _closeCheck.Stop();
        _ownsPanel = false;
        Interlocked.Exchange(ref _claimUntil, 0);
        RestoreForeground();
        return "closed";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closeCheck.Stop();
        if (_panelEvents != 0) DockNativeMethods.UnhookWinEvent(_panelEvents);
        _panelEvents = 0;
        _lifetime.Cancel();
        RestoreForeground();
        // A UIA call that never returned still holds the token; leave it to the GC.
        if (_uiaCall is null or { IsCompleted: true }) _lifetime.Dispose();
    }
}
