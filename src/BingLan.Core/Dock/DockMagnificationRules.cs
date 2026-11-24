using BingLan.Core.Models;

namespace BingLan.Core.Dock;

/// <summary>
/// Pure cosine-falloff magnification rules for macOS-style dock icon hover zoom.
/// All calculations stay in DIPs and never mutate layout geometry; callers apply the
/// resulting scale and vertical lift strictly through <c>RenderTransform</c>.
/// </summary>
public static class DockMagnificationRules
{
    /// <summary>Half-width of the cosine influence bell measured in dock item cells.</summary>
    public const double InfluenceRadiusCells = 2.2d;

    /// <summary>Extra transparent headroom above the max magnified icon so scaling never clips.</summary>
    public const double OverflowSafetyDip = 10d;

    /// <summary>Vertical lift ratio of the extra icon height under Standard motion.</summary>
    private const double StandardLiftFraction = 0.16d;

    public static double ClampScale(double scale) =>
        double.IsFinite(scale)
            ? Math.Clamp(scale, DockState.MinimumHoverMagnificationScale, DockState.MaximumHoverMagnificationScale)
            : DockState.DefaultHoverMagnificationScale;

    /// <summary>Default description shown for the Dock hover magnification toggle.</summary>
    public const string DefaultSettingDescription =
        "鼠标靠近 Dock 图标时按距离平滑放大（macOS 风格），遵守动效与系统动画设置";

    /// <summary>
    /// Explains why an enabled hover magnification toggle currently has no effect, or returns
    /// <c>null</c> when magnification is disabled or ready to run.
    /// </summary>
    public static string? InactiveReason(
        bool enabled,
        MotionLevel motion,
        bool systemAnimationsEnabled)
    {
        if (!enabled)
        {
            return null;
        }

        if (!systemAnimationsEnabled && motion == MotionLevel.Off)
        {
            return "当前 Windows 已关闭动画效果，且下方「尺寸与高级 → 动效」设为关闭，悬停放大暂不生效（请开启 Windows「设置 → 辅助功能 → 视觉效果 → 动画效果」并将动效改为标准或减弱）";
        }

        if (!systemAnimationsEnabled)
        {
            return "当前 Windows 已关闭动画效果，悬停放大暂不生效（可在 Windows「设置 → 辅助功能 → 视觉效果 → 动画效果」中开启）";
        }

        if (motion == MotionLevel.Off)
        {
            return "当前「动效」已关闭，悬停放大暂不生效（可在下方「尺寸与高级 → 动效」中改为标准或减弱）";
        }

        return null;
    }

    public static string SettingDescription(
        bool enabled,
        MotionLevel motion,
        bool systemAnimationsEnabled) =>
        InactiveReason(enabled, motion, systemAnimationsEnabled) ?? DefaultSettingDescription;

    public static string ToggleStatusMessage(
        bool enabled,
        MotionLevel motion,
        bool systemAnimationsEnabled)
    {
        if (!enabled)
        {
            return "已关闭 Dock 悬停放大";
        }

        if (!systemAnimationsEnabled && motion == MotionLevel.Off)
        {
            return "已开启悬停放大，但 Windows 动画与 Dock 动效均已关闭，暂不生效";
        }

        if (!systemAnimationsEnabled)
        {
            return "已开启悬停放大，但 Windows 已关闭动画效果，暂不生效";
        }

        if (motion == MotionLevel.Off)
        {
            return "已开启悬停放大，但 Dock「动效」已关闭，暂不生效";
        }

        return "已开启 Dock 悬停放大";
    }

    /// <summary>
    /// Effective peak scale after honouring the feature toggle, Dock motion level, and the
    /// OS "show animations" preference. Returns 1 when magnification must stay off.
    /// </summary>
    public static double EffectiveMaxScale(
        bool enabled,
        double configuredMaxScale,
        MotionLevel motion,
        bool systemAnimationsEnabled)
    {
        if (!enabled || !systemAnimationsEnabled || motion == MotionLevel.Off)
        {
            return 1d;
        }

        return ClampScale(configuredMaxScale);
    }

    /// <summary>
    /// Cosine-bell scale factor for an icon whose centre is <paramref name="distanceDip"/>
    /// away from the pointer along the dock axis.
    /// </summary>
    public static double ComputeScale(double distanceDip, double influenceRadiusDip, double maxScale)
    {
        if (maxScale <= 1d
            || influenceRadiusDip <= 0d
            || !double.IsFinite(distanceDip)
            || !double.IsFinite(influenceRadiusDip)
            || !double.IsFinite(maxScale))
        {
            return 1d;
        }

        var absDistance = Math.Abs(distanceDip);
        if (absDistance >= influenceRadiusDip)
        {
            return 1d;
        }

        var weight = 0.5d * (1d + Math.Cos(Math.PI * (absDistance / influenceRadiusDip)));
        return 1d + ((maxScale - 1d) * weight);
    }

    /// <summary>
    /// Subtle upward translate (negative Y in DIPs) for the magnified icon.
    /// Strictly 0 under Reduced or Off motion ("减弱档缩短时长且不位移").
    /// </summary>
    public static double ComputeLiftDip(double scale, double iconSizeDip, MotionLevel motion)
    {
        if (motion != MotionLevel.Standard || scale <= 1d || iconSizeDip <= 0d)
        {
            return 0d;
        }

        return -((scale - 1d) * iconSizeDip * StandardLiftFraction);
    }

    /// <summary>Exponential smoothing rate (1/s) toward target scale; Reduced converges faster.</summary>
    public static double SmoothingRatePerSecond(MotionLevel motion) => motion switch
    {
        MotionLevel.Standard => 18d,
        MotionLevel.Reduced => 34d,
        _ => double.PositiveInfinity
    };

    /// <summary>Duration of the settle-back animation when the pointer leaves the dock.</summary>
    public static TimeSpan ReturnDuration(MotionLevel motion) => motion switch
    {
        MotionLevel.Standard => TimeSpan.FromMilliseconds(130),
        MotionLevel.Reduced => TimeSpan.FromMilliseconds(70),
        _ => TimeSpan.Zero
    };

    /// <summary>
    /// Transparent vertical headroom (in DIPs) needed above the dock surface so the peak
    /// magnified icon and upward lift stay inside the window without changing AppBar thickness.
    /// </summary>
    public static double RequiredTopOverflowDip(
        bool enabled,
        double configuredMaxScale,
        double iconSizeDip,
        MotionLevel motion,
        bool systemAnimationsEnabled)
    {
        var maxScale = EffectiveMaxScale(enabled, configuredMaxScale, motion, systemAnimationsEnabled);
        if (maxScale <= 1d || iconSizeDip <= 0d)
        {
            return 0d;
        }

        var growthDip = (maxScale - 1d) * iconSizeDip;
        var liftDip = Math.Abs(ComputeLiftDip(maxScale, iconSizeDip, motion));
        return Math.Ceiling(growthDip + liftDip + OverflowSafetyDip);
    }

    /// <summary>
    /// Returns true when magnification slots should be reset and any residual transform
    /// animations detached: whenever magnification is off/disabling, or transitioning from
    /// disabled to enabled at runtime so prior non-magnification hover zoom clocks cannot
    /// block cosine magnification. Staying enabled preserves live hover state.
    /// </summary>
    public static bool ShouldResetSlotsOnOverflowChange(
        double previousTopOverflowDip,
        double nextTopOverflowDip) =>
        nextTopOverflowDip <= 0d || previousTopOverflowDip <= 0d;

    /// <summary>
    /// Computes the Win32 window hit region (in window-local physical pixels) for the dock window.
    /// While idle with transparent top headroom, excludes the headroom strictly via <c>SetWindowRgn</c>
    /// so cross-process clicks and <c>WindowFromPoint</c> hit underlying applications directly;
    /// returns <c>null</c> (unclipped full window) only while magnification or morph animation is active.
    /// </summary>
    public static PixelRect? ComputeHitRegionRect(
        int windowWidthPx,
        int windowHeightPx,
        int topOverflowPx,
        bool isMagnificationActive,
        bool isMorphAnimating) =>
        ComputeHitRegionRect(
            windowWidthPx,
            windowHeightPx,
            DockEdge.Bottom,
            topOverflowPx,
            isMagnificationActive,
            isMorphAnimating);

    public static PixelRect? ComputeHitRegionRect(
        int windowWidthPx,
        int windowHeightPx,
        DockEdge edge,
        int overflowPx,
        bool isMagnificationActive,
        bool isMorphAnimating)
    {
        if (windowWidthPx <= 0
            || windowHeightPx <= 0
            || overflowPx <= 0
            || isMagnificationActive
            || isMorphAnimating)
        {
            return null;
        }

        var normalized = DockEdgeRules.Normalize(edge);
        if (normalized == DockEdge.Left)
        {
            var clamped = Math.Clamp(overflowPx, 0, Math.Max(0, windowWidthPx - 1));
            return clamped > 0 ? new PixelRect(0, 0, windowWidthPx - clamped, windowHeightPx) : null;
        }

        if (normalized == DockEdge.Right)
        {
            var clamped = Math.Clamp(overflowPx, 0, Math.Max(0, windowWidthPx - 1));
            return clamped > 0 ? new PixelRect(clamped, 0, windowWidthPx, windowHeightPx) : null;
        }

        var clampedTop = Math.Clamp(overflowPx, 0, Math.Max(0, windowHeightPx - 1));
        if (clampedTop <= 0)
        {
            return null;
        }

        return new PixelRect(0, clampedTop, windowWidthPx, windowHeightPx);
    }

    /// <summary>
    /// Computes the screen bounds and local Y offset for the native glass backdrop window so it
    /// covers only the dock surface body (never the transparent magnification headroom) unless a
    /// morph animation needs the full bounding box.
    /// </summary>
    public static (PixelRect Bounds, int TopInsetPx) ComputeBackdropSurfaceBounds(
        PixelRect windowBounds,
        int surfaceTopOffsetPx,
        bool isMorphAnimating)
    {
        var (bounds, _, topInset) = ComputeBackdropSurfaceBounds(
            windowBounds,
            surfaceLeftOffsetPx: 0,
            surfaceTopOffsetPx,
            surfaceWidthPx: 0,
            surfaceHeightPx: 0,
            isMorphAnimating);
        return (bounds, topInset);
    }

    /// <summary>
    /// Generalised backdrop cropping across all dock edges: trims transparent magnification
    /// headroom on any side (top for bottom dock, right for left dock, left for right dock)
    /// while leaving morph animations unclipped.
    /// </summary>
    public static (PixelRect Bounds, int LeftInsetPx, int TopInsetPx) ComputeBackdropSurfaceBounds(
        PixelRect windowBounds,
        int surfaceLeftOffsetPx,
        int surfaceTopOffsetPx,
        int surfaceWidthPx,
        int surfaceHeightPx,
        bool isMorphAnimating)
    {
        if (isMorphAnimating || !windowBounds.HasArea)
        {
            return (windowBounds, 0, 0);
        }

        var leftInset = Math.Clamp(surfaceLeftOffsetPx, 0, Math.Max(0, windowBounds.Width - 1));
        var topInset = Math.Clamp(surfaceTopOffsetPx, 0, Math.Max(0, windowBounds.Height - 1));
        var availableWidth = windowBounds.Width - leftInset;
        var availableHeight = windowBounds.Height - topInset;
        var width = surfaceWidthPx > 0 ? Math.Clamp(surfaceWidthPx, 1, availableWidth) : availableWidth;
        var height = surfaceHeightPx > 0 ? Math.Clamp(surfaceHeightPx, 1, availableHeight) : availableHeight;

        var cropped = new PixelRect(
            windowBounds.Left + leftInset,
            windowBounds.Top + topInset,
            windowBounds.Left + leftInset + width,
            windowBounds.Top + topInset + height);
        return (cropped, leftInset, topInset);
    }
}
