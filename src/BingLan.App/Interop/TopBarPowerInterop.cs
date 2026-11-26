using System.Runtime.InteropServices;
using BingLan.Core.TopBar;

namespace BingLan.App.Interop;

/// <summary>
/// Battery details, Windows 10/11 power-mode overlay switching (powrprof.dll) and built-in
/// laptop display brightness (\\.\LCD IOCTL). Fails cleanly and reports unsupported on desktops
/// or systems where an optional API is absent.
/// </summary>
internal static class TopBarPowerInterop
{
    private static readonly Guid BetterBatteryOverlay = new("961CC777-2547-4F9D-8174-7D86181B8A7A");
    private static readonly Guid BestPerformanceOverlay = new("DED574B5-45A0-4F42-8737-46345C09C238");

    private const uint GenericRead = 0x80000000u;
    private const uint GenericWrite = 0x40000000u;
    private const uint FileShareRead = 0x1u;
    private const uint FileShareWrite = 0x2u;
    private const uint OpenExisting = 3u;
    private const uint IoctlVideoQuerySupportedBrightness = 0x00230494u;
    private const uint IoctlVideoQueryDisplayBrightness = 0x00230498u;
    private const uint IoctlVideoSetDisplayBrightness = 0x0023049Cu;
    private const byte DisplayBrightnessPolicyAc = 1;
    private const byte DisplayBrightnessPolicyDc = 2;

    private delegate uint PowerGetEffectiveOverlaySchemeDelegate(out Guid effectiveOverlayPolicyGuid);
    private delegate uint PowerSetActiveOverlaySchemeDelegate(Guid overlaySchemeGuid);

    private static readonly Lazy<(PowerGetEffectiveOverlaySchemeDelegate? Get, PowerSetActiveOverlaySchemeDelegate? Set)> PowerOverlayApi =
        new(LoadPowerOverlayApi);

    internal static TopBarBatterySnapshot ReadBatterySnapshot()
    {
        var status = new TopBarNativeMethods.SystemPowerStatus();
        var hasPowerStatus = TopBarNativeMethods.GetSystemPowerStatus(ref status);
        var percent = hasPowerStatus && TopBarModuleRules.IsBatteryPercentKnown(status.BatteryLifePercent)
            ? status.BatteryLifePercent
            : -1;
        var batteryStatus = hasPowerStatus
            ? TopBarModuleRules.ClassifyBattery(status.AcLineStatus, status.BatteryFlag)
            : TopBarBatteryStatus.Unknown;
        var isPowerSaver = hasPowerStatus && (status.SystemStatusFlag & 1) != 0;
        int? remainingSeconds = hasPowerStatus && status.BatteryLifeTime > 0
            ? status.BatteryLifeTime
            : null;

        var (powerModeSupported, powerMode) = ReadPowerMode();
        var onAc = hasPowerStatus && status.AcLineStatus == 1;
        var (brightnessSupported, brightnessPercent) = ReadBuiltInBrightness(onAc);

        return new TopBarBatterySnapshot(
            Percent: percent,
            Status: batteryStatus,
            IsPowerSaver: isPowerSaver,
            RemainingSeconds: remainingSeconds,
            PowerModeSupported: powerModeSupported,
            PowerMode: powerMode,
            BrightnessSupported: brightnessSupported,
            BrightnessPercent: brightnessPercent);
    }

    internal static (bool Supported, TopBarPowerMode Mode) ReadPowerMode()
    {
        var api = PowerOverlayApi.Value;
        if (api.Get is null || api.Set is null)
        {
            return (false, TopBarPowerMode.Unknown);
        }

        try
        {
            if (api.Get(out var guid) != 0)
            {
                return (false, TopBarPowerMode.Unknown);
            }

            if (guid == BetterBatteryOverlay)
            {
                return (true, TopBarPowerMode.BestPowerEfficiency);
            }
            if (guid == BestPerformanceOverlay)
            {
                return (true, TopBarPowerMode.BestPerformance);
            }
            if (guid == Guid.Empty)
            {
                return (true, TopBarPowerMode.Balanced);
            }
            return (true, TopBarPowerMode.Unknown);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return (false, TopBarPowerMode.Unknown);
        }
    }

    internal static bool SetPowerMode(TopBarPowerMode mode)
    {
        var api = PowerOverlayApi.Value;
        if (api.Set is null)
        {
            return false;
        }

        try
        {
            if (mode == TopBarPowerMode.BestPowerEfficiency)
            {
                return api.Set(BetterBatteryOverlay) == 0;
            }
            if (mode == TopBarPowerMode.BestPerformance)
            {
                return api.Set(BestPerformanceOverlay) == 0;
            }
            if (mode == TopBarPowerMode.Balanced)
            {
                return api.Set(Guid.Empty) == 0;
            }
            return false;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            return false;
        }
    }

    internal static (bool Supported, int Percent) ReadBuiltInBrightness(bool onAc)
    {
        var handle = CreateFileW(
            @"\\.\LCD",
            GenericRead,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            0,
            0);
        if (handle == 0 || handle == -1)
        {
            return (false, 0);
        }

        try
        {
            var brightness = new DisplayBrightness();
            if (!DeviceIoControlQuery(
                    handle,
                    IoctlVideoQueryDisplayBrightness,
                    0,
                    0,
                    out brightness,
                    (uint)Marshal.SizeOf<DisplayBrightness>(),
                    out _,
                    0))
            {
                return (false, 0);
            }

            var value = onAc ? brightness.AcBrightness : brightness.DcBrightness;
            return (true, TopBarControlRules.ClampPercent(value));
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    internal static bool SetBuiltInBrightness(int percent)
    {
        var target = (byte)TopBarControlRules.ClampPercent(percent);
        var handle = CreateFileW(
            @"\\.\LCD",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            0,
            OpenExisting,
            0,
            0);
        if (handle == 0 || handle == -1)
        {
            return false;
        }

        try
        {
            if (TrySnapToSupportedBrightness(handle, target) is not { } level)
            {
                return false;
            }
            var current = new DisplayBrightness();
            var hasCurrent = DeviceIoControlQuery(
                handle,
                IoctlVideoQueryDisplayBrightness,
                0,
                0,
                out current,
                (uint)Marshal.SizeOf<DisplayBrightness>(),
                out _,
                0);
            var status = new TopBarNativeMethods.SystemPowerStatus();
            var onAc = !TopBarNativeMethods.GetSystemPowerStatus(ref status) || status.AcLineStatus == 1;
            var brightness = new DisplayBrightness
            {
                DisplayPolicy = onAc ? DisplayBrightnessPolicyAc : DisplayBrightnessPolicyDc,
                AcBrightness = onAc ? level : (hasCurrent ? current.AcBrightness : level),
                DcBrightness = onAc ? (hasCurrent ? current.DcBrightness : level) : level
            };
            return DeviceIoControlSet(
                handle,
                IoctlVideoSetDisplayBrightness,
                ref brightness,
                (uint)Marshal.SizeOf<DisplayBrightness>(),
                0,
                0,
                out _,
                0);
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static byte? TrySnapToSupportedBrightness(nint handle, byte target)
    {
        var supported = new byte[256];
        if (!DeviceIoControlSupported(
                handle,
                IoctlVideoQuerySupportedBrightness,
                0,
                0,
                supported,
                (uint)supported.Length,
                out var returned,
                0)
            || returned == 0)
        {
            return null;
        }

        var count = (int)Math.Min(returned, (uint)supported.Length);
        var best = supported[0];
        var bestDiff = Math.Abs(best - target);
        for (var i = 1; i < count; i++)
        {
            var diff = Math.Abs(supported[i] - target);
            if (diff < bestDiff)
            {
                best = supported[i];
                bestDiff = diff;
            }
        }
        return best;
    }

    private static (PowerGetEffectiveOverlaySchemeDelegate? Get, PowerSetActiveOverlaySchemeDelegate? Set) LoadPowerOverlayApi()
    {
        if (!NativeLibrary.TryLoad("powrprof.dll", out var module))
        {
            return (null, null);
        }
        if (!NativeLibrary.TryGetExport(module, "PowerGetEffectiveOverlayScheme", out var getPtr)
            || !NativeLibrary.TryGetExport(module, "PowerSetActiveOverlayScheme", out var setPtr))
        {
            return (null, null);
        }
        return (
            Marshal.GetDelegateForFunctionPointer<PowerGetEffectiveOverlaySchemeDelegate>(getPtr),
            Marshal.GetDelegateForFunctionPointer<PowerSetActiveOverlaySchemeDelegate>(setPtr));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayBrightness
    {
        internal byte DisplayPolicy;
        internal byte AcBrightness;
        internal byte DcBrightness;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlQuery(
        nint device,
        uint ioControlCode,
        nint inBuffer,
        uint inBufferSize,
        out DisplayBrightness outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlSupported(
        nint device,
        uint ioControlCode,
        nint inBuffer,
        uint inBufferSize,
        [Out] byte[] outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControlSet(
        nint device,
        uint ioControlCode,
        ref DisplayBrightness inBuffer,
        uint inBufferSize,
        nint outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
