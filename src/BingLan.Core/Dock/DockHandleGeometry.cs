namespace BingLan.Core.Dock;

/// <summary>
/// Geometry for the small handle that represents a smart-hidden dock: it sits in the
/// corner of the dock's monitor, can be dragged anywhere inside the work area, and the
/// dock expands next to it when the pointer hovers over it. All methods work in pixels;
/// callers convert DIPs with the monitor scale the way the AppBar controller does.
/// </summary>
public static class DockHandleGeometry
{
    public const double HandleSizeDip = 44;
    public const double HandleMarginDip = 16;

    /// <summary>Gap between the handle and the dock row expanded beside it, in DIPs.</summary>
    public const double ExpansionGapDip = 8;

    public static int HandleSizePixels(double scale) =>
        Math.Max(1, (int)Math.Round(HandleSizeDip * scale));

    public static int HandleMarginPixels(double scale) =>
        Math.Max(0, (int)Math.Round(HandleMarginDip * scale));

    public static int ExpansionGapPixels(double scale) =>
        Math.Max(0, (int)Math.Round(ExpansionGapDip * scale));

    /// <summary>
    /// The handle's default spot: the work area's right end, on the same band as the
    /// dock row (bottom edge at the work-area bottom minus the dock's edge gap).
    /// </summary>
    public static PixelRect Default(PixelRect workArea, int handleSizePixels, int marginPixels, int edgeGapPixels) =>
        Default(workArea, handleSizePixels, marginPixels, edgeGapPixels, DockEdge.Bottom);

    public static PixelRect Default(
        PixelRect workArea,
        int handleSizePixels,
        int marginPixels,
        int edgeGapPixels,
        DockEdge edge)
    {
        if (!workArea.HasArea)
        {
            return new PixelRect(0, 0, handleSizePixels, handleSizePixels);
        }

        var safeGap = Math.Max(0, edgeGapPixels);
        var normalized = DockEdgeRules.Normalize(edge);
        if (normalized == DockEdge.Left)
        {
            var left = Math.Min(Math.Max(workArea.Left, workArea.Right - handleSizePixels), workArea.Left + safeGap);
            var bottom = Math.Max(workArea.Top + handleSizePixels, workArea.Bottom - marginPixels);
            return new PixelRect(left, bottom - handleSizePixels, left + handleSizePixels, bottom);
        }
        if (normalized == DockEdge.Right)
        {
            var right = Math.Max(workArea.Left + handleSizePixels, workArea.Right - safeGap);
            var bottom = Math.Max(workArea.Top + handleSizePixels, workArea.Bottom - marginPixels);
            return new PixelRect(right - handleSizePixels, bottom - handleSizePixels, right, bottom);
        }

        var defaultRight = Math.Max(workArea.Left + handleSizePixels, workArea.Right - marginPixels);
        var defaultBottom = Math.Max(workArea.Top + handleSizePixels, workArea.Bottom - safeGap);
        return new PixelRect(defaultRight - handleSizePixels, defaultBottom - handleSizePixels, defaultRight, defaultBottom);
    }

    /// <summary>Keeps a dragged handle fully inside the work area, preserving its size.</summary>
    public static PixelRect ClampToWorkArea(PixelRect handle, PixelRect workArea)
    {
        if (!workArea.HasArea)
        {
            return handle;
        }

        var width = Math.Max(1, handle.Width);
        var height = Math.Max(1, handle.Height);
        var left = Math.Clamp(handle.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - width));
        var top = Math.Clamp(handle.Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - height));
        return new PixelRect(left, top, left + width, top + height);
    }

    /// <summary>
    /// The dock row shown beside the handle: bottoms aligned with the handle, extending
    /// toward the monitor centre (left of a right-half handle, right of a left-half one)
    /// and clamped to the work area.
    /// </summary>
    public static PixelRect ExpandedDock(
        PixelRect handle,
        PixelRect workArea,
        int dockThicknessPixels,
        int contentLengthPixels,
        int gapPixels) =>
        ExpandedDock(handle, workArea, dockThicknessPixels, contentLengthPixels, gapPixels, DockEdge.Bottom);

    public static PixelRect ExpandedDock(
        PixelRect handle,
        PixelRect workArea,
        int dockThicknessPixels,
        int contentLengthPixels,
        int gapPixels,
        DockEdge edge)
    {
        if (!workArea.HasArea)
        {
            return handle;
        }

        var normalized = DockEdgeRules.Normalize(edge);
        var thickness = Math.Max(1, dockThicknessPixels);
        if (DockEdgeRules.IsVertical(normalized))
        {
            var maxVerticalLength = Math.Max(1, (int)Math.Round(workArea.Height * DockLayoutMetrics.HandleMaxExtentFraction));
            var verticalLength = Math.Clamp(contentLengthPixels, 1, maxVerticalLength);
            int verticalLeft;
            if (normalized == DockEdge.Left)
            {
                verticalLeft = Math.Clamp(handle.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - thickness));
            }
            else
            {
                var verticalRight = Math.Clamp(handle.Right, Math.Min(workArea.Right, workArea.Left + thickness), workArea.Right);
                verticalLeft = Math.Max(workArea.Left, verticalRight - thickness);
            }

            var handleCentreY = (handle.Top + handle.Bottom) / 2;
            var workAreaCentreY = (workArea.Top + workArea.Bottom) / 2;
            int verticalTop;
            if (handleCentreY >= workAreaCentreY)
            {
                var verticalBottom = Math.Max(workArea.Top + verticalLength, handle.Top - gapPixels);
                verticalTop = verticalBottom - verticalLength;
            }
            else
            {
                verticalTop = Math.Min(workArea.Bottom - verticalLength, handle.Bottom + gapPixels);
            }

            verticalTop = Math.Clamp(verticalTop, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - verticalLength));
            return new PixelRect(verticalLeft, verticalTop, Math.Min(workArea.Right, verticalLeft + thickness), verticalTop + verticalLength);
        }

        // The expanded row grows from the handle and may cross the screen, so it keeps
        // the stricter cap instead of the dock's wider bottom-edge limit.
        var maxLength = Math.Max(1, (int)Math.Round(workArea.Width * DockLayoutMetrics.HandleMaxExtentFraction));
        var length = Math.Clamp(contentLengthPixels, 1, maxLength);
        var bottom = Math.Min(handle.Bottom, workArea.Bottom);
        var top = Math.Max(workArea.Top, bottom - thickness);
        bottom = Math.Min(workArea.Bottom, top + thickness);

        var handleCentre = (handle.Left + handle.Right) / 2;
        var workAreaCentre = (workArea.Left + workArea.Right) / 2;
        int left;
        if (handleCentre >= workAreaCentre)
        {
            var right = Math.Max(workArea.Left + length, handle.Left - gapPixels);
            left = right - length;
        }
        else
        {
            left = Math.Min(workArea.Right - length, handle.Right + gapPixels);
        }

        return new PixelRect(left, top, left + length, bottom);
    }
}
