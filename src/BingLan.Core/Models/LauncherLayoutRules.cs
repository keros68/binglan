namespace BingLan.Core.Models;

public readonly record struct LauncherPanelBounds(
    double DipWidth,
    double DipHeight,
    double NavigationDipWidth,
    int PixelLeft,
    int PixelTop,
    int PixelWidth,
    int PixelHeight);

/// <summary>
/// Responsive size and centered placement rules for the Ice Blue Overview Card
/// across laptop, desktop, ultrawide, portrait, and mixed-DPI monitors.
/// </summary>
public static class LauncherLayoutRules
{
    public const double MinLandscapeWidth = 760d;
    public const double MaxLandscapeWidth = 1080d;
    public const double MinLandscapeHeight = 480d;
    public const double MaxLandscapeHeight = 720d;

    public const double MinPortraitWidth = 520d;
    public const double MaxPortraitWidth = 900d;
    public const double MinPortraitHeight = 520d;
    public const double MaxPortraitHeight = 820d;

    public static LauncherPanelBounds ComputeBounds(
        int workLeft, int workTop, int workWidth, int workHeight, double dpi)
    {
        var safeWorkW = Math.Max(1, workWidth);
        var safeWorkH = Math.Max(1, workHeight);
        var scale = double.IsFinite(dpi) && dpi > 0 ? Math.Max(1d, dpi / 96d) : 1d;

        var workDipW = safeWorkW / scale;
        var workDipH = safeWorkH / scale;

        var marginX = Math.Clamp(workDipW * 0.04d, 16d, 48d);
        var marginY = Math.Clamp(workDipH * 0.05d, 16d, 48d);
        var maxDipW = Math.Max(120d, workDipW - marginX * 2d);
        var maxDipH = Math.Max(120d, workDipH - marginY * 2d);

        double targetW;
        double targetH;
        if (workDipH > workDipW)
        {
            targetW = Math.Clamp(workDipW * 0.80d, MinPortraitWidth, MaxPortraitWidth);
            targetH = Math.Clamp(workDipH * 0.50d, MinPortraitHeight, MaxPortraitHeight);
        }
        else
        {
            targetW = Math.Clamp(workDipW * 0.58d, MinLandscapeWidth, MaxLandscapeWidth);
            targetH = Math.Clamp(workDipH * 0.66d, MinLandscapeHeight, MaxLandscapeHeight);
        }

        var dipW = Math.Min(workDipW, Math.Min(targetW, maxDipW));
        var dipH = Math.Min(workDipH, Math.Min(targetH, maxDipH));
        var navDipW = Math.Clamp(dipW * 0.165d, 112d, 164d);

        var pixelW = Math.Clamp((int)Math.Round(dipW * scale), 1, safeWorkW);
        var pixelH = Math.Clamp((int)Math.Round(dipH * scale), 1, safeWorkH);
        var pixelLeft = workLeft + Math.Max(0, (safeWorkW - pixelW) / 2);
        var pixelTop = workTop + Math.Max(0, (safeWorkH - pixelH) / 2);

        return new LauncherPanelBounds(dipW, dipH, navDipW, pixelLeft, pixelTop, pixelW, pixelH);
    }
}
