using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// Keeps a window reachable after monitors are removed or rearranged: a window that no
/// longer shows at least a minimum part on any work area is moved into the nearest one.
/// </summary>
public static class PlacementResolver
{
    public static PixelRect EnsureVisible(
        PixelRect desired,
        IReadOnlyList<PixelRect> workAreas,
        PixelRect fallback,
        int minimumVisiblePixels = 64)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (!desired.HasArea)
        {
            throw new ArgumentException("Desired bounds must have a positive area.", nameof(desired));
        }
        if (minimumVisiblePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(minimumVisiblePixels),
                minimumVisiblePixels,
                "Minimum visible pixels must be positive.");
        }

        var validWorkAreas = workAreas.Where(area => area.HasArea).ToList();
        if (validWorkAreas.Any(area => HasMinimumVisibility(desired, area, minimumVisiblePixels)))
        {
            return desired;
        }

        var target = SelectTargetWorkArea(desired, validWorkAreas, fallback);
        return ClampInside(desired, target);
    }

    /// <summary>
    /// Resolves the bounds a card should occupy when shown: recalls a remembered spot for the
    /// live monitor arrangement when present (keeping at least a minimum part on screen), or
    /// pulls an unremembered out-of-bounds card cleanly into the nearest work area.
    /// </summary>
    public static PixelRect ResolveShownBounds(
        PixelRect currentBounds,
        WindowPlacement? placement,
        string? currentLayout,
        IReadOnlyList<PixelRect> workAreas,
        PixelRect fallback,
        int minimumWidth = 1,
        int minimumHeight = 1,
        int minimumVisiblePixels = 64)
    {
        ArgumentNullException.ThrowIfNull(workAreas);
        if (!currentBounds.HasArea)
        {
            return currentBounds;
        }

        var minWidth = Math.Max(1, minimumWidth);
        var minHeight = Math.Max(1, minimumHeight);
        if (placement is not null
            && !string.IsNullOrEmpty(currentLayout)
            && DisplayLayoutRules.TryRecall(placement, currentLayout, out var remembered))
        {
            var recalled = new PixelRect(
                remembered.Left,
                remembered.Top,
                remembered.Left + Math.Max(remembered.Width, minWidth),
                remembered.Top + Math.Max(remembered.Height, minHeight));
            return EnsureVisible(recalled, workAreas, fallback, minimumVisiblePixels);
        }

        var validWorkAreas = workAreas.Where(area => area.HasArea).ToList();
        var sized = new PixelRect(
            currentBounds.Left,
            currentBounds.Top,
            currentBounds.Left + Math.Max(currentBounds.Width, minWidth),
            currentBounds.Top + Math.Max(currentBounds.Height, minHeight));
        if (validWorkAreas.Any(area => FitsInside(sized, area)))
        {
            return sized;
        }

        var target = SelectTargetWorkArea(sized, validWorkAreas, fallback);
        return ClampInside(sized, target);
    }

    private static PixelRect SelectTargetWorkArea(
        PixelRect desired,
        List<PixelRect> validWorkAreas,
        PixelRect fallback)
    {
        if (validWorkAreas.Count > 0)
        {
            return validWorkAreas
                .Select((area, index) => new
                {
                    Area = area,
                    Index = index,
                    Distance = DistanceSquared(desired, area)
                })
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.Index)
                .First()
                .Area;
        }

        if (fallback.HasArea)
        {
            return fallback;
        }

        throw new ArgumentException(
            "At least one work area or a valid fallback must have a positive area.",
            nameof(validWorkAreas));
    }

    private static bool FitsInside(PixelRect bounds, PixelRect area) =>
        bounds.Left >= area.Left
        && bounds.Right <= area.Right
        && bounds.Top >= area.Top
        && bounds.Bottom <= area.Bottom;

    /// <summary>
    /// Moves a window out of the screen edges the taskbar or a dock has reserved: the
    /// edges where the work area is smaller than the monitor. An edge without a
    /// reservation is left alone, so a window parked partly off-screen stays there.
    /// </summary>
    public static PixelRect KeepOutOfReservedEdges(PixelRect bounds, PixelRect monitor, PixelRect workArea)
    {
        var left = bounds.Left;
        var top = bounds.Top;
        if (workArea.Bottom < monitor.Bottom && bounds.Bottom > workArea.Bottom)
        {
            top = Math.Max(workArea.Top, workArea.Bottom - bounds.Height);
        }
        if (workArea.Top > monitor.Top && top < workArea.Top)
        {
            top = workArea.Top;
        }
        if (workArea.Right < monitor.Right && bounds.Right > workArea.Right)
        {
            left = Math.Max(workArea.Left, workArea.Right - bounds.Width);
        }
        if (workArea.Left > monitor.Left && left < workArea.Left)
        {
            left = workArea.Left;
        }
        return new PixelRect(left, top, left + bounds.Width, top + bounds.Height);
    }

    private static bool HasMinimumVisibility(
        PixelRect desired,
        PixelRect workArea,
        int minimumVisiblePixels)
    {
        var visibleWidth = Math.Max(
            0,
            Math.Min(desired.Right, workArea.Right) - Math.Max(desired.Left, workArea.Left));
        var visibleHeight = Math.Max(
            0,
            Math.Min(desired.Bottom, workArea.Bottom) - Math.Max(desired.Top, workArea.Top));

        return visibleWidth >= Math.Min(minimumVisiblePixels, desired.Width)
            && visibleHeight >= Math.Min(minimumVisiblePixels, desired.Height);
    }

    internal static PixelRect ClampInside(PixelRect desired, PixelRect target)
    {
        var width = Math.Min(desired.Width, target.Width);
        var height = Math.Min(desired.Height, target.Height);
        var left = Math.Clamp(desired.Left, target.Left, target.Right - width);
        var top = Math.Clamp(desired.Top, target.Top, target.Bottom - height);
        return new PixelRect(left, top, left + width, top + height);
    }

    internal static long DistanceSquared(PixelRect first, PixelRect second)
    {
        var horizontal = first.Right < second.Left
            ? (long)second.Left - first.Right
            : second.Right < first.Left
                ? (long)first.Left - second.Right
                : 0L;
        var vertical = first.Bottom < second.Top
            ? (long)second.Top - first.Bottom
            : second.Bottom < first.Top
                ? (long)first.Top - second.Bottom
                : 0L;

        return (horizontal * horizontal) + (vertical * vertical);
    }
}
