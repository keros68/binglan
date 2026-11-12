using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using BingLan.App.Interop;

namespace BingLan.App.Taskbar;

/// <summary>Opens the Windows-owned overflow panel on demand without persistent hooks or polling.</summary>
internal sealed class TrayOverflowController : IDisposable
{
    private const uint GaRoot = 2;
    private const int ClaimWindowMs = 2000;
    private const string PanelClass = "TopLevelWindowForOverflowXamlIsland";
    private static readonly TimeSpan UiaWait = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SessionPollInterval = TimeSpan.FromMilliseconds(200);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly int _slowUiaMs;
    private readonly Action<string> _log;
    private DispatcherTimer? _sessionTimer;
    private Task<string>? _uiaCall;
    // The button matched while opening; focus is inside the panel when closing it.
    private int[]? _chevronRuntimeId;
    private uint _explorerProcessId;
    private nint _previousForeground;
    private long _claimUntilTick;
    private int _opening;
    private bool _ownsPanel;
    private bool _stopping;
    private bool _disposed;

    /// <param name="slowUiaMs">Test-only delay before each UIA toggle, to exercise late cancellation and hangs.</param>
    internal TrayOverflowController(int slowUiaMs = 0, Action<string>? log = null)
    {
        _slowUiaMs = slowUiaMs;
        _log = log ?? (message => System.Diagnostics.Debug.WriteLine(message));
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

    /// <summary>True while the panel on screen was opened by this controller.</summary>
    internal bool OwnsPanel => _ownsPanel && IsVisible;

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
            _ownsPanel = false;
            var tray = TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null);
            if (tray == 0) return "taskbar-missing";
            TrackExplorerInstance(tray);
            // Win+B injected toward an elevated window would be dropped without an error.
            if (ShellSurfaceNativeMethods.IsForegroundElevated()) return "elevated-foreground";
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
                if (invoked != "invoked")
                {
                    if (invoked == "pending") StartSessionMonitor();
                    return invoked;
                }
                _ownsPanel = true;
                StartSessionMonitor();
                for (var check = 0; check < 20; check++)
                {
                    // Settle the accepted toggle even during cancellation;
                    // CloseAsync must observe the resulting native surface.
                    await Task.Delay(100);
                    if (!IsVisible) continue;
                    opened = true;
                    Interlocked.Exchange(ref _claimUntilTick, 0);
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
            var claimPending = IsInvoking || Environment.TickCount64 <= Interlocked.Read(ref _claimUntilTick);
            if (!opened && !IsVisible && !claimPending)
            {
                _ownsPanel = false;
                _sessionTimer?.Stop();
                if (inputSent) await RestoreAfterInputAsync();
                else RestoreForeground();
            }
            Volatile.Write(ref _opening, 0);
        }
    }

    private void TrackExplorerInstance(nint tray)
    {
        DockNativeMethods.GetWindowThreadProcessId(tray, out var processId);
        if (processId == _explorerProcessId) return;
        _explorerProcessId = processId;
        Volatile.Write(ref _chevronRuntimeId, null);
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
        _dispatcher.VerifyAccess();
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
            var elements = tray.FindAll(TreeScope.Descendants, new OrCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, PinnedIconId)));
            var focused = AutomationElement.FocusedElement;
            var buttons = new List<TrayButtonInfo>(elements.Count);
            var focusedIndex = -1;
            var rememberedIndex = -1;
            var remembered = Volatile.Read(ref _chevronRuntimeId);
            for (var index = 0; index < elements.Count; index++)
            {
                var current = elements[index].Current;
                var bounds = current.BoundingRectangle;
                buttons.Add(new TrayButtonInfo(current.AutomationId, current.ClassName, current.Name,
                    bounds.IsEmpty ? double.NaN : bounds.Left, bounds.IsEmpty ? double.NaN : bounds.Right));
                if (focusedIndex < 0 && focused is not null && Automation.Compare(elements[index], focused))
                    focusedIndex = index;
                if (rememberedIndex < 0 && remembered is not null
                    && elements[index].GetRuntimeId().AsSpan().SequenceEqual(remembered))
                    rememberedIndex = index;
            }
            var chevron = FindOverflowChevron(buttons, focusedIndex, rememberedIndex, out var match);
            if (chevron < 0) return "missing";
            var button = elements[chevron];
            if (button.Current.IsEnabled
                && button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)
                && pattern is InvokePattern invoke)
            {
                if (cancellationToken.IsCancellationRequested) return "cancelled";
                // The panel may have closed or opened on its own during the search;
                // a toggle now would reverse the user's or Explorer's change.
                if (IsVisible != expectVisible) return "state-changed";
                Volatile.Write(ref _chevronRuntimeId, button.GetRuntimeId());
                _log($"OVERFLOW invoke expect-visible={expectVisible} match={match}");
                if (!expectVisible) Interlocked.Exchange(ref _claimUntilTick, Environment.TickCount64 + ClaimWindowMs);
                invoke.Invoke();
                if (!expectVisible) Interlocked.Exchange(ref _claimUntilTick, Environment.TickCount64 + ClaimWindowMs);
                return "invoked";
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

    internal readonly record struct TrayButtonInfo(string AutomationId, string ClassName, string Name, double Left, double Right);

    private const string ChevronId = "SystemTrayIcon";
    private const string ChevronClass = "SystemTray.NormalButton";
    private const string PinnedIconId = "NotifyItemIcon";
    // Verified localized names; other languages use the structural match below.
    private static readonly string[] KnownChevronNames = ["显示隐藏的图标", "Show hidden icons"];

    /// <summary>
    /// Index of the "show hidden icons" button in <paramref name="buttons"/> (tree order),
    /// or -1. A known name matches directly. Otherwise three independent facts must agree,
    /// so an unknown structure fails instead of toggling another icon (the IME mode
    /// button shares the same id and class): Win+B focused it, it is the first such
    /// button in the tree, and it sits before and outside the pinned app icons.
    /// <paramref name="rememberedIndex"/> is the element this controller already toggled
    /// in the current Explorer instance; closing reuses it because focus is in the panel.
    /// </summary>
    internal static int FindOverflowChevron(
        IReadOnlyList<TrayButtonInfo> buttons, int focusedIndex, int rememberedIndex, out string match)
    {
        match = "name";
        for (var index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            if (button.AutomationId == ChevronId && button.ClassName == ChevronClass
                && KnownChevronNames.Contains(button.Name, StringComparer.OrdinalIgnoreCase)) return index;
        }
        match = "remembered";
        if (rememberedIndex >= 0 && rememberedIndex < buttons.Count
            && buttons[rememberedIndex].AutomationId == ChevronId
            && buttons[rememberedIndex].ClassName == ChevronClass) return rememberedIndex;
        match = "focus";
        if (focusedIndex < 0 || focusedIndex >= buttons.Count) return -1;
        var focused = buttons[focusedIndex];
        if (focused.AutomationId != ChevronId || focused.ClassName != ChevronClass) return -1;
        var firstCandidate = -1;
        var firstPinned = -1;
        for (var index = 0; index < buttons.Count; index++)
        {
            if (firstCandidate < 0 && buttons[index].AutomationId == ChevronId && buttons[index].ClassName == ChevronClass)
                firstCandidate = index;
            if (firstPinned < 0 && buttons[index].AutomationId == PinnedIconId) firstPinned = index;
        }
        // Without pinned icons the chevron cannot be told apart from the IME mode button.
        if (firstCandidate != focusedIndex || firstPinned < 0 || focusedIndex > firstPinned) return -1;
        var pinned = buttons.Where(button => button.AutomationId == PinnedIconId
            && !double.IsNaN(button.Left) && !double.IsNaN(button.Right)).ToList();
        if (pinned.Count == 0 || double.IsNaN(focused.Left) || double.IsNaN(focused.Right)) return -1;
        // Left of all pinned icons, or right of all of them on a right-to-left taskbar.
        var outside = focused.Right <= pinned.Min(button => button.Left) + 1
            || focused.Left >= pinned.Max(button => button.Right) - 1;
        return outside ? focusedIndex : -1;
    }

    /// <summary>Close path used by toggle and cycle checks; same gates as exit cleanup.</summary>
    internal async Task<bool> CloseOwnedPanelAsync()
    {
        if (!IsVisible)
        {
            _ownsPanel = false;
            Interlocked.Exchange(ref _claimUntilTick, 0);
            _sessionTimer?.Stop();
            return true;
        }
        var result = await InvokeAsync(expectVisible: true, CancellationToken.None);
        if (result is "invoked" or "state-changed")
        {
            _ownsPanel = false;
            Interlocked.Exchange(ref _claimUntilTick, 0);
            _sessionTimer?.Stop();
            RestoreForeground();
            return true;
        }
        return false;
    }

    // Active only while a panel session or late-open claim window is live; idle otherwise.
    private void StartSessionMonitor()
    {
        if (_disposed || _stopping) return;
        if (_sessionTimer is null)
        {
            _sessionTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = SessionPollInterval
            };
            _sessionTimer.Tick += (_, _) => MonitorSession();
        }
        _sessionTimer.Start();
    }

    private void MonitorSession()
    {
        if (_disposed || _stopping)
        {
            _sessionTimer?.Stop();
            return;
        }
        if (Volatile.Read(ref _opening) != 0) return;

        var visible = IsVisible;
        var claimActive = IsInvoking || Environment.TickCount64 <= Interlocked.Read(ref _claimUntilTick);
        if (visible)
        {
            if (!_ownsPanel && claimActive)
            {
                _ownsPanel = true;
                Interlocked.Exchange(ref _claimUntilTick, 0);
            }
            return;
        }

        if (_ownsPanel)
        {
            _ownsPanel = false;
            Interlocked.Exchange(ref _claimUntilTick, 0);
            _sessionTimer?.Stop();
            RestoreForeground();
            return;
        }

        if (!claimActive)
        {
            _sessionTimer?.Stop();
            RestoreForeground();
        }
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
        var previous = _previousForeground;
        _previousForeground = 0;
        // A user who has switched to another app, or opened a tray icon's menu,
        // keeps that focus. A destroyed original window is not re-activated.
        if (previous == 0 || !DockNativeMethods.IsWindow(previous)) return;
        var foreground = DockNativeMethods.GetForegroundWindow();
        var panel = TaskbarNativeMethods.FindWindowW(PanelClass, null);
        var shellForeground = IsTaskbarForeground() || BelongsTo(foreground, panel);
        if (!_disposed && IsInvoking && (foreground == previous || shellForeground))
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
        _lifetime.Cancel();
        for (var check = 0; check < 60 && Volatile.Read(ref _opening) != 0; check++)
            await Task.Delay(50);
        if (Volatile.Read(ref _opening) != 0) return "open-pending";
        if (_uiaCall is { IsCompleted: false } call && await Task.WhenAny(call, Task.Delay(UiaWait)) != call)
            return "open-pending";

        if (_ownsPanel && IsVisible)
        {
            var result = await InvokeAsync(expectVisible: true, CancellationToken.None);
            if (result == "pending") return "close-pending";
            if (result is not ("invoked" or "state-changed")) return result;
            for (var check = 0; check < 20 && IsVisible; check++) await Task.Delay(50);
            if (IsVisible) return "close-unconfirmed";
        }
        _ownsPanel = false;
        Interlocked.Exchange(ref _claimUntilTick, 0);
        _sessionTimer?.Stop();
        RestoreForeground();
        return "closed";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionTimer?.Stop();
        _lifetime.Cancel();
        RestoreForeground();
        // A UIA call that never returned still holds the token; leave it to the GC.
        if (_uiaCall is null or { IsCompleted: true }) _lifetime.Dispose();
    }
}
