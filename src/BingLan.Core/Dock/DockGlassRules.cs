namespace BingLan.Core.Dock;

/// <summary>
/// Pure rules controlling when a floating surface may attach the native Windows 11
/// Composition backdrop and how strongly its tint is applied over that backdrop.
/// </summary>
public static class DockGlassRules
{
    /// <summary>
    /// Attenuation applied when a dock follows the shared card glass density so the
    /// default light-blue dock stays translucent over real backdrop blur. Custom dock
    /// colours opt into full tint opacity so high densities (for example 96% white)
    /// render bright rather than washed into dark gray over dark backgrounds.
    /// </summary>
    public const double SharedBackdropTintScale = 0.24d;

    /// <summary>
    /// Alpha 1 (<c>#01FFFFFF</c>) is the invisible hit-test sentinel used by clear / no-backing
    /// surfaces; real-time backdrop blur only attaches when a visible surface tint is present.
    /// </summary>
    public const byte MinimumBackdropSurfaceAlpha = 2;

    public static bool ShouldUseCompositionBackdrop(
        bool isWindows11OrLater,
        bool frostedGlassEnabled,
        bool transparencyEnabled,
        bool batterySaverActive,
        bool highContrastEnabled,
        byte surfaceAlpha) =>
        isWindows11OrLater
        && frostedGlassEnabled
        && transparencyEnabled
        && !batterySaverActive
        && !highContrastEnabled
        && surfaceAlpha >= MinimumBackdropSurfaceAlpha;

    public static byte ComputeBackdropTintAlpha(byte surfaceAlpha, bool fullTintOpacity) =>
        fullTintOpacity
            ? surfaceAlpha
            : (byte)Math.Clamp(Math.Round(surfaceAlpha * SharedBackdropTintScale), 0, 255);
}
