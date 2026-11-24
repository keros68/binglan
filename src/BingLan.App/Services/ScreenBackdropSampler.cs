using System.Runtime.InteropServices;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Services;

/// <summary>
/// Samples a tiny 1-pixel strip from the screen off the UI thread so transparent or near-clear
/// top bars and docks automatically switch between light and dark ink as static or dynamic
/// wallpapers change brightness.
/// </summary>
internal static class ScreenBackdropSampler
{
    private const int DefaultSampleCount = 16;

    /// <summary>
    /// Samples the top 1-pixel row of the top bar (above all text, icons, search box and bottom
    /// hairline) and removes the bar's known translucent surface tint to recover the wallpaper.
    /// </summary>
    internal static ColorContrastRules.SrgbColor? SampleTopBarBackdrop(
        PixelRect barBounds,
        string surfaceWithAlpha)
    {
        if (!barBounds.HasArea)
        {
            return null;
        }

        var sampleY = barBounds.Height > 2 ? barBounds.Top + 1 : barBounds.Top;
        var strip = new BackdropSampleStrip(barBounds.Left, sampleY, barBounds.Width, 1);
        var observed = SampleStrip(strip);
        return observed is { } color
            ? BarAdaptiveInkRules.UncompositeOverSurface(color, surfaceWithAlpha)
            : null;
    }

    /// <summary>
    /// Samples a 1-pixel strip immediately outside the dock window inside its screen-edge band,
    /// avoiding all dock glass, icons and borders.
    /// </summary>
    internal static ColorContrastRules.SrgbColor? SampleDockBackdrop(
        PixelRect dockBounds,
        PixelRect monitorBounds,
        DockEdge edge)
    {
        var strip = BarAdaptiveInkRules.ComputeDockSampleStrip(dockBounds, monitorBounds, edge);
        return strip is { } target ? SampleStrip(target) : null;
    }

    internal static ColorContrastRules.SrgbColor? SampleStrip(
        BackdropSampleStrip strip,
        int sampleCount = DefaultSampleCount)
    {
        if (strip.Width <= 0 || strip.Height <= 0)
        {
            return null;
        }

        var pixelCount = strip.Width * strip.Height;
        if (pixelCount <= 0 || pixelCount > 32768)
        {
            return null;
        }

        nint screenDc = 0;
        nint memoryDc = 0;
        nint bitmap = 0;
        nint previousObject = 0;
        try
        {
            screenDc = BackdropSamplingNativeMethods.GetDC(0);
            if (screenDc == 0)
            {
                return null;
            }

            memoryDc = BackdropSamplingNativeMethods.CreateCompatibleDC(screenDc);
            if (memoryDc == 0)
            {
                return null;
            }

            var info = new BackdropSamplingNativeMethods.BitmapInfo
            {
                Header = new BackdropSamplingNativeMethods.BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BackdropSamplingNativeMethods.BitmapInfoHeader>(),
                    Width = strip.Width,
                    Height = -strip.Height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = BackdropSamplingNativeMethods.BiRgb
                }
            };

            bitmap = BackdropSamplingNativeMethods.CreateDIBSection(
                memoryDc,
                ref info,
                BackdropSamplingNativeMethods.DibRgbColors,
                out var bits,
                0,
                0);
            if (bitmap == 0 || bits == 0)
            {
                return null;
            }

            previousObject = BackdropSamplingNativeMethods.SelectObject(memoryDc, bitmap);
            if (!BackdropSamplingNativeMethods.BitBlt(
                    memoryDc,
                    0,
                    0,
                    strip.Width,
                    strip.Height,
                    screenDc,
                    strip.Left,
                    strip.Top,
                    BackdropSamplingNativeMethods.Srccopy))
            {
                return null;
            }

            var targetSamples = Math.Clamp(sampleCount, 1, pixelCount);
            var samples = new ColorContrastRules.SrgbColor[targetSamples];
            // Skip the outer 5% margins on long strips so corner shadows or rounded edges do not skew.
            var start = pixelCount >= 20 ? pixelCount / 20 : 0;
            var end = Math.Max(start, pixelCount - 1 - start);
            for (var i = 0; i < targetSamples; i++)
            {
                var index = targetSamples == 1
                    ? (start + end) / 2
                    : start + (int)Math.Round((end - start) * (i / (double)(targetSamples - 1)));
                var bgra = Marshal.ReadInt32(bits, index * 4);
                var b = (bgra & 0xFF) / 255d;
                var g = ((bgra >> 8) & 0xFF) / 255d;
                var r = ((bgra >> 16) & 0xFF) / 255d;
                samples[i] = new ColorContrastRules.SrgbColor(r, g, b, 1d);
            }

            return BarAdaptiveInkRules.PickRepresentativeBackdropColor(samples);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (memoryDc != 0 && previousObject != 0)
            {
                BackdropSamplingNativeMethods.SelectObject(memoryDc, previousObject);
            }
            if (bitmap != 0)
            {
                BackdropSamplingNativeMethods.DeleteObject(bitmap);
            }
            if (memoryDc != 0)
            {
                BackdropSamplingNativeMethods.DeleteDC(memoryDc);
            }
            if (screenDc != 0)
            {
                BackdropSamplingNativeMethods.ReleaseDC(0, screenDc);
            }
        }
    }
}
