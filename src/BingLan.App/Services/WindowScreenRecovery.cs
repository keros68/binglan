using System.Windows;
using System.Windows.Interop;
using BingLan.App.Dock;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Services;

/// <summary>
/// Moves a desktop card back into view when a monitor is disconnected or the
/// resolution or scaling changes and the card no longer shows on any work area.
/// </summary>
internal static class WindowScreenRecovery
{
    private const int MinimumVisiblePixels = 64;

    // The arrangement of monitors the cards were last placed for. Null until startup has
    // placed them; while it differs from the live arrangement, a display change is still
    // being handled and moves made by Windows or by the recovery are not remembered.
    private static string? _settledLayout;
    private static bool _layoutChangeRaised;

    /// <summary>
    /// Raised once when a card moves on an arrangement of monitors that has not been
    /// settled yet, for changes that arrive without a display-settings event.
    /// </summary>
    internal static event Action? LayoutChanged;

    internal static string CurrentLayout() =>
        DisplayLayoutRules.Key(MonitorCatalog.GetAll().Select(monitor => (monitor.Bounds, monitor.Dpi)));

    /// <summary>Starts remembering card positions for the arrangement now in use.</summary>
    internal static void SettleLayout()
    {
        _settledLayout = CurrentLayout();
        _layoutChangeRaised = false;
    }

    internal static void RememberLayout(Window window, WindowPlacement placement)
    {
        if (_settledLayout is null)
        {
            return;
        }

        var layout = CurrentLayout();
        if (layout != _settledLayout)
        {
            if (!_layoutChangeRaised)
            {
                _layoutChangeRaised = true;
                LayoutChanged?.Invoke();
            }
            return;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0 && DockNativeMethods.GetWindowRect(handle, out var native))
        {
            DisplayLayoutRules.Remember(
                placement,
                layout,
                new PixelRect(native.Left, native.Top, native.Right, native.Bottom));
        }
    }

    /// <summary>Puts a card back where it last sat on the arrangement of monitors now in use.</summary>
    internal static bool RestoreLayout(Window window, WindowPlacement placement)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !DisplayLayoutRules.TryRecall(placement, CurrentLayout(), out var target))
        {
            return false;
        }

        var moved = false;
        // Moving onto a monitor with other scaling resizes the window for that scaling;
        // the second pass puts back the size remembered on it.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!DockNativeMethods.GetWindowRect(handle, out var native)
                || new PixelRect(native.Left, native.Top, native.Right, native.Bottom) == target)
            {
                break;
            }

            moved |= NativeMethods.SetWindowPos(
                handle,
                0,
                target.Left,
                target.Top,
                target.Width,
                target.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }
        return moved;
    }

    /// <summary>Whether the placement remembers a spot for the arrangement of monitors now in use.</summary>
    internal static bool HasCurrentLayout(WindowPlacement placement) =>
        DisplayLayoutRules.TryRecall(placement, CurrentLayout(), out _);

    /// <summary>
    /// Gathers cards that meet an arrangement of monitors for the first time: cards
    /// without a remembered spot move fully into the nearest work area, cascaded so
    /// they do not stack on one another or on the settled cards. Works while a card
    /// is hidden, so a monitor lost in the meantime does not leave it off-screen
    /// when it is shown again.
    /// </summary>
    internal static void GatherOnArrival(IEnumerable<Window> arrivals, IEnumerable<Window> settled)
    {
        var monitors = MonitorCatalog.GetAll();
        if (monitors.Count == 0)
        {
            return;
        }

        PixelRect? ReadRect(Window window)
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == 0 || !DockNativeMethods.GetWindowRect(handle, out var native))
            {
                return null;
            }
            var rect = new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
            return rect.HasArea ? rect : null;
        }

        var reserved = new List<PixelRect>();
        foreach (var window in settled)
        {
            if (ReadRect(window) is { } spot)
            {
                reserved.Add(spot);
            }
        }

        var entries = new List<(nint Handle, PixelRect Current, Window Window)>();
        foreach (var window in arrivals)
        {
            if (ReadRect(window) is { } current)
            {
                entries.Add((new WindowInteropHelper(window).Handle, current, window));
            }
        }
        if (entries.Count == 0)
        {
            return;
        }

        // The card's own WPF minimum, in pixels of the primary monitor, is how far an
        // overcrowded gather may shrink it; smaller would be pushed back by the window.
        var primaryMonitor = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        var scale = primaryMonitor.Dpi / 96d;
        (int Width, int Height)? MinimumOf(Window window) =>
            window.MinWidth > 0 && window.MinHeight > 0
                ? ((int)Math.Ceiling(window.MinWidth * scale), (int)Math.Ceiling(window.MinHeight * scale))
                : null;
        var minimumSizes = entries
            .Select(entry => MinimumOf(entry.Window))
            .ToList();

        var targets = DisplayArrivalRules.Gather(
            entries.Select(entry => entry.Current).ToList(),
            monitors.Select(monitor => monitor.WorkingArea).ToArray(),
            primaryMonitor.WorkingArea,
            reserved,
            minimumSizes);
        for (var index = 0; index < entries.Count; index++)
        {
            var target = targets[index];
            if (target == entries[index].Current)
            {
                continue;
            }
            NativeMethods.SetWindowPos(
                entries[index].Handle,
                0,
                target.Left,
                target.Top,
                target.Width,
                target.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
        }
    }

    internal static bool EnsureOnScreen(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        // Hidden cards are recovered too: a monitor may have been unplugged while the
        // desktop was toggled away, and the spot it comes back to must already be fixed.
        if (handle == 0 || !DockNativeMethods.GetWindowRect(handle, out var native))
        {
            return false;
        }

        var monitors = MonitorCatalog.GetAll();
        if (monitors.Count == 0)
        {
            return false;
        }

        var current = new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
        if (!current.HasArea)
        {
            return false;
        }

        var primary = monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? monitors[0];
        var target = PlacementResolver.EnsureVisible(
            current,
            monitors.Select(monitor => monitor.WorkingArea).ToArray(),
            primary.WorkingArea,
            MinimumVisiblePixels);
        if (target == current)
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            handle,
            0,
            target.Left,
            target.Top,
            target.Width,
            target.Height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>
    /// Moves a card that the taskbar or the dock now covers back into the work area.
    /// Called when the work area changes, for example when the dock starts reserving
    /// the bottom of the screen.
    /// </summary>
    internal static bool KeepOutOfReservedEdges(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !window.IsVisible || !DockNativeMethods.GetWindowRect(handle, out var native))
        {
            return false;
        }

        var current = new PixelRect(native.Left, native.Top, native.Right, native.Bottom);
        var target = ReservedEdgeTarget(current);
        return target is { } moved && moved != current && NativeMethods.SetWindowPos(
            handle,
            0,
            moved.Left,
            moved.Top,
            0,
            0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate);
    }

    /// <summary>
    /// Where a window at these bounds belongs once the reserved edges of the monitor
    /// under its centre are kept clear; null when no monitor is under it.
    /// </summary>
    internal static PixelRect? ReservedEdgeTarget(PixelRect bounds)
    {
        var centerX = (bounds.Left + bounds.Right) / 2;
        var centerY = (bounds.Top + bounds.Bottom) / 2;
        var monitor = MonitorCatalog.GetAll().FirstOrDefault(candidate =>
            centerX >= candidate.Bounds.Left && centerX < candidate.Bounds.Right &&
            centerY >= candidate.Bounds.Top && centerY < candidate.Bounds.Bottom);
        return monitor is null || !bounds.HasArea
            ? null
            : PlacementResolver.KeepOutOfReservedEdges(bounds, monitor.Bounds, monitor.WorkingArea);
    }
}
