using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Threading;
using BingLan.App.Interop;

/// <summary>Opt-in compatibility experiment; no production entry or taskbar setting writes.</summary>
internal sealed class TrayOverflowExperiment : IDisposable
{
    private const uint GaRoot = 2;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _closeCheck;
    private nint _previousForeground;
    private int _opening;
    private bool _ownsPanel;
    private bool _stopping;
    private bool _disposed;

    internal TrayOverflowExperiment()
    {
        // Experimental polling runs only during an explicitly opened panel session.
        // Production integration requires Explorer close events and more build coverage.
        _closeCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _closeCheck.Tick += (_, _) => { if (!IsVisible) RestoreForeground(); };
    }

    internal static bool IsVisible
    {
        get
        {
            var window = TaskbarNativeMethods.FindWindowW("TopLevelWindowForOverflowXamlIsland", null);
            return window != 0 && DockNativeMethods.IsWindowVisible(window);
        }
    }

    internal async Task<string> OpenAsync(bool invokeButton = true)
    {
        if (_disposed || _stopping) return "disposed";
        if (Interlocked.CompareExchange(ref _opening, 1, 0) != 0) return "busy";
        var opened = false;
        var inputSent = false;
        var cancellationToken = _lifetime.Token;
        try
        {
            if (IsVisible) return "already-open";
            if (TaskbarNativeMethods.FindWindowW("Shell_TrayWnd", null) == 0) return "taskbar-missing";
            _closeCheck.Stop();
            _previousForeground = DockNativeMethods.GetForegroundWindow();
            if (!ShellSurfaceNativeMethods.SendChord(0x5B, 0x42)) return "rejected";
            inputSent = true;
            if (!invokeButton) return "injected-failure";
            for (var attempt = 0; attempt < 15; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                if (!IsTaskbarForeground())
                {
                    if (DockNativeMethods.GetForegroundWindow() == _previousForeground) continue;
                    return "focus-changed";
                }
                if (await Task.Run(InvokeButton))
                {
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
            }
            return "button-missing";
        }
        catch (OperationCanceledException) { return "cancelled"; }
        finally
        {
            if (!opened && !IsVisible)
            {
                if (inputSent) await RestoreAfterInputAsync();
                else RestoreForeground();
            }
            Volatile.Write(ref _opening, 0);
        }
    }

    internal static bool InvokeButton()
    {
        try
        {
            var tray = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"));
            if (tray is null) return false;
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
                    invoke.Invoke();
                    return true;
                }
            }
        }
        catch (Exception exception) when (exception is ElementNotAvailableException
            or ElementNotEnabledException or InvalidOperationException or COMException)
        {
            Console.WriteLine($"OVERFLOW UIA unavailable={exception.GetType().Name}");
        }
        return false;
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
        _closeCheck.Stop();
        // A user who has switched to another app keeps that app's focus.
        var foreground = DockNativeMethods.GetForegroundWindow();
        var panel = TaskbarNativeMethods.FindWindowW("TopLevelWindowForOverflowXamlIsland", null);
        if (_previousForeground != 0 && (IsTaskbarForeground() || BelongsTo(foreground, panel)))
        {
            Console.WriteLine($"OVERFLOW restore-focus={DockNativeMethods.SetForegroundWindow(_previousForeground)}");
        }
        _previousForeground = 0;
        _ownsPanel = false;
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
                Console.WriteLine($"OVERFLOW preserve-focus previous={_previousForeground} current={foreground}");
                LogWindow(foreground);
                break;
            }
            await Task.Delay(50);
        }
        RestoreForeground();
    }

    private static void LogWindow(nint window)
    {
        var name = new System.Text.StringBuilder(256);
        DockNativeMethods.GetClassNameW(window, name, name.Capacity);
        DockNativeMethods.GetWindowThreadProcessId(window, out var processId);
        DockNativeMethods.GetWindowRect(window, out var rect);
        Console.WriteLine($"OVERFLOW foreground class={name} pid={processId} rect={rect.Left},{rect.Top},{rect.Right},{rect.Bottom} root={DockNativeMethods.GetAncestor(window, GaRoot)}");
    }

    internal async Task<bool> CloseAsync()
    {
        _stopping = true;
        _closeCheck.Stop();
        _lifetime.Cancel();
        // Keep the host HWND alive until pending input and UIA work have settled.
        for (var check = 0; check < 40 && Volatile.Read(ref _opening) != 0; check++)
            await Task.Delay(50);
        if (Volatile.Read(ref _opening) != 0) return false;

        var owned = _ownsPanel;
        if (owned && IsVisible)
        {
            // Focus restoration alone does not collapse this XAML surface on the
            // verified build. Use the same native toggle that closes it normally.
            if (!await Task.Run(InvokeButton)) return false;
            for (var check = 0; check < 20 && IsVisible; check++) await Task.Delay(50);
            if (IsVisible) return false;
        }
        RestoreForeground();
        return !owned || !IsVisible;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        RestoreForeground();
        _lifetime.Dispose();
    }
}
