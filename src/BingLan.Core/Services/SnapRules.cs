using BingLan.Core.Dock;

namespace BingLan.Core.Services;

public enum SnapGuideKind
{
    Align,
    Spacing
}

/// <summary>A guide to draw while dragging: vertical when X1 == X2, horizontal otherwise.</summary>
public readonly record struct SnapGuide(int X1, int Y1, int X2, int Y2, SnapGuideKind Kind);

public sealed record SnapResult(PixelRect Bounds, IReadOnlyList<SnapGuide> Guides);

/// <summary>
/// Snaps a card being dragged to the work-area edges, to the matching edges of nearby
/// cards and to a standard gap beside them. Horizontal and vertical snapping are
/// independent. Cards farther away than the reach are ignored, so a card crossing open
/// desktop moves freely instead of catching on every other card's edges.
/// </summary>
public static class SnapRules
{
    public static SnapResult Snap(
        PixelRect moving,
        IReadOnlyList<PixelRect> others,
        PixelRect workArea,
        int threshold,
        int gap,
        int reach)
    {
        ArgumentNullException.ThrowIfNull(others);
        others = others.Where(other => IsWithinReach(moving, other, reach)).ToArray();

        var horizontal = BestOffset(
            moving.Left,
            moving.Right,
            Candidates(workArea.Left, workArea.Right, others.Select(other => (other.Left, other.Right)), gap),
            threshold);
        var vertical = BestOffset(
            moving.Top,
            moving.Bottom,
            Candidates(workArea.Top, workArea.Bottom, others.Select(other => (other.Top, other.Bottom)), gap),
            threshold);

        var snapped = new PixelRect(
            moving.Left + horizontal.Offset,
            moving.Top + vertical.Offset,
            moving.Right + horizontal.Offset,
            moving.Bottom + vertical.Offset);

        var guides = new List<SnapGuide>();
        if (horizontal.Line is { } x)
        {
            var span = VerticalSpan(snapped, others, workArea, horizontal.Kind);
            guides.Add(new SnapGuide(x, span.Start, x, span.End, horizontal.Kind));
        }
        if (vertical.Line is { } y)
        {
            var span = HorizontalSpan(snapped, others, workArea, vertical.Kind);
            guides.Add(new SnapGuide(span.Start, y, span.End, y, vertical.Kind));
        }
        return new SnapResult(snapped, guides);
    }

    /// <summary>
    /// Snaps a card being dragged onto the desktop grid: the leading edges land on the
    /// nearest grid line of a lattice anchored at the work-area corner, and the size is
    /// untouched. Used instead of <see cref="Snap"/> while the user has grid alignment
    /// on; guides are replaced by the grid the overlay draws. A non-positive cell keeps
    /// the position as given.
    /// </summary>
    public static PixelRect SnapToGrid(PixelRect moving, int originX, int originY, int cell)
    {
        if (cell <= 0)
        {
            return moving;
        }

        var left = originX + (int)Math.Round((moving.Left - originX) / (double)cell) * cell;
        var top = originY + (int)Math.Round((moving.Top - originY) / (double)cell) * cell;
        return new PixelRect(left, top, left + (moving.Right - moving.Left), top + (moving.Bottom - moving.Top));
    }

    private static List<(int Target, bool Leading, SnapGuideKind Kind)> Candidates(
        int areaStart,
        int areaEnd,
        IEnumerable<(int Start, int End)> others,
        int gap)
    {
        // Leading edges (left/top) and trailing edges (right/bottom) of the moving card.
        var candidates = new List<(int Target, bool Leading, SnapGuideKind Kind)>
        {
            (areaStart, true, SnapGuideKind.Align),
            (areaEnd, false, SnapGuideKind.Align)
        };
        foreach (var (start, end) in others)
        {
            candidates.Add((start, true, SnapGuideKind.Align));
            candidates.Add((end, false, SnapGuideKind.Align));
            candidates.Add((end + gap, true, SnapGuideKind.Spacing));
            candidates.Add((start - gap, false, SnapGuideKind.Spacing));
        }
        return candidates;
    }

    private static bool IsWithinReach(PixelRect moving, PixelRect other, int reach) =>
        Math.Max(other.Left - moving.Right, moving.Left - other.Right) <= reach &&
        Math.Max(other.Top - moving.Bottom, moving.Top - other.Bottom) <= reach;

    private static (int Offset, int? Line, SnapGuideKind Kind) BestOffset(
        int leading,
        int trailing,
        IEnumerable<(int Target, bool Leading, SnapGuideKind Kind)> candidates,
        int threshold)
    {
        (int Offset, int? Line, SnapGuideKind Kind) best = (0, null, SnapGuideKind.Align);
        var bestDistance = threshold + 1;
        foreach (var (target, isLeading, kind) in candidates)
        {
            var offset = target - (isLeading ? leading : trailing);
            if (Math.Abs(offset) < bestDistance)
            {
                bestDistance = Math.Abs(offset);
                best = (offset, target, kind);
            }
        }
        return best;
    }

    private static (int Start, int End) VerticalSpan(
        PixelRect snapped,
        IReadOnlyList<PixelRect> others,
        PixelRect workArea,
        SnapGuideKind kind) =>
        kind == SnapGuideKind.Spacing || others.Count == 0
            ? (snapped.Top, snapped.Bottom)
            : (Math.Max(workArea.Top, Math.Min(snapped.Top, others.Min(other => other.Top))),
                Math.Min(workArea.Bottom, Math.Max(snapped.Bottom, others.Max(other => other.Bottom))));

    private static (int Start, int End) HorizontalSpan(
        PixelRect snapped,
        IReadOnlyList<PixelRect> others,
        PixelRect workArea,
        SnapGuideKind kind) =>
        kind == SnapGuideKind.Spacing || others.Count == 0
            ? (snapped.Left, snapped.Right)
            : (Math.Max(workArea.Left, Math.Min(snapped.Left, others.Min(other => other.Left))),
                Math.Min(workArea.Right, Math.Max(snapped.Right, others.Max(other => other.Right))));
}
