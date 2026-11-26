using System.Collections.Concurrent;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using BingLan.App.Interop;
using BingLan.Core.Models;
using BingLan.Core.TopBar;
using Microsoft.Win32;

namespace BingLan.App.TopBar;

/// <summary>
/// Reads the read-only system facts the top bar shows: battery, volume, network and the
/// current keyboard layout. Every call is cheap enough to ride the shared sampling tick
/// or a background thread; no method keeps state or a timer of its own.
/// </summary>
internal static class TopBarSystemInfo
{
    internal static (int Percent, TopBarBatteryStatus Status) ReadBattery()
    {
        var status = new TopBarNativeMethods.SystemPowerStatus();
        if (!TopBarNativeMethods.GetSystemPowerStatus(ref status))
        {
            return (-1, TopBarBatteryStatus.Unknown);
        }

        // 128 and 255 mean the level is unknown; the classification flags do the rest.
        var percent = TopBarModuleRules.IsBatteryPercentKnown(status.BatteryLifePercent)
            ? status.BatteryLifePercent
            : -1;
        return (percent, TopBarModuleRules.ClassifyBattery(status.AcLineStatus, status.BatteryFlag));
    }

    internal static (int Percent, bool Muted)? ReadVolume()
    {
        var volume = AudioEndpointVolumeInterop.ReadMasterVolume();
        return volume is { } value
            ? (TopBarModuleRules.VolumePercentFromScalar(value.Scalar), value.Muted)
            : null;
    }

    internal static TopBarAudioSnapshot ReadAudioSnapshot() =>
        AudioEndpointVolumeInterop.ReadAudioSnapshot();

    internal static bool SetMasterVolume(int percent) =>
        AudioEndpointVolumeInterop.SetMasterVolume(TopBarControlRules.ClampPercent(percent) / 100f);

    internal static bool SetMasterMute(bool muted) =>
        AudioEndpointVolumeInterop.SetMasterMute(muted);

    internal static bool SetDefaultOutputDevice(string deviceId) =>
        AudioEndpointVolumeInterop.SetDefaultOutputDevice(deviceId);

    internal static bool SetAudioSessionVolume(string? deviceId, uint processId, int percent) =>
        AudioEndpointVolumeInterop.SetSessionVolume(deviceId, processId, TopBarControlRules.ClampPercent(percent) / 100f);

    internal static bool SetAudioSessionMute(string? deviceId, uint processId, bool muted) =>
        AudioEndpointVolumeInterop.SetSessionMute(deviceId, processId, muted);

    internal static TopBarBatterySnapshot ReadBatterySnapshot() =>
        TopBarPowerInterop.ReadBatterySnapshot();

    internal static bool SetPowerMode(TopBarPowerMode mode) =>
        TopBarPowerInterop.SetPowerMode(mode);

    internal static bool SetBuiltInBrightness(int percent) =>
        TopBarPowerInterop.SetBuiltInBrightness(percent);

    internal static TopBarNetworkSnapshot ReadNetworkSnapshot() =>
        TopBarWifiInterop.ReadNetworkSnapshot();

    internal static bool ConnectSavedWifi(Guid interfaceGuid, string profileName) =>
        TopBarWifiInterop.ConnectSavedProfile(interfaceGuid, profileName);

    internal static bool DisconnectWifi(Guid interfaceGuid) =>
        TopBarWifiInterop.Disconnect(interfaceGuid);

    internal static bool TriggerWifiScan(Guid interfaceGuid) =>
        TopBarWifiInterop.TriggerScan(interfaceGuid);

    internal static TopBarNetworkStatus ReadNetwork()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(candidate => candidate.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && candidate.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(candidate => (candidate.OperationalStatus == OperationalStatus.Up,
                    candidate.NetworkInterfaceType
                        is NetworkInterfaceType.Wireless80211
                        or NetworkInterfaceType.Wman))
                .ToList();
            var (connected, label) = TopBarModuleRules.ClassifyNetwork(interfaces);
            if (!connected)
            {
                return TopBarModuleRules.DescribeNetwork(false, false);
            }

            var isWireless = string.Equals(label, "WLAN", StringComparison.Ordinal);
            if (!isWireless)
            {
                return TopBarModuleRules.DescribeNetwork(true, false);
            }

            var (ssid, signalQuality) = ReadActiveWifiInfo();
            return TopBarModuleRules.DescribeNetwork(true, true, ssid, signalQuality, showWifiName: false);
        }
        catch (NetworkInformationException)
        {
            return TopBarModuleRules.DescribeNetwork(false, false);
        }
    }

    internal static (string? Ssid, int? SignalQuality) ReadActiveWifiInfo()
    {
        nint clientHandle = 0;
        nint listPtr = 0;
        try
        {
            if (TopBarNativeMethods.WlanOpenHandle(
                    TopBarNativeMethods.WlanClientVersionVista,
                    0,
                    out _,
                    out clientHandle) != 0
                || clientHandle == 0)
            {
                return (null, null);
            }

            if (TopBarNativeMethods.WlanEnumInterfaces(clientHandle, 0, out listPtr) != 0
                || listPtr == 0)
            {
                return (null, null);
            }

            var count = Marshal.ReadInt32(listPtr);
            if (count <= 0)
            {
                return (null, null);
            }

            var itemSize = Marshal.SizeOf<TopBarNativeMethods.WlanInterfaceInfo>();
            var firstItemPtr = listPtr + 8;
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<TopBarNativeMethods.WlanInterfaceInfo>(
                    firstItemPtr + i * itemSize);
                if (info.State != TopBarNativeMethods.WlanInterfaceStateConnected)
                {
                    continue;
                }

                nint connPtr = 0;
                try
                {
                    var guid = info.InterfaceGuid;
                    if (TopBarNativeMethods.WlanQueryInterface(
                            clientHandle,
                            ref guid,
                            TopBarNativeMethods.WlanIntfOpcodeCurrentConnection,
                            0,
                            out var dataSize,
                            out connPtr,
                            0) != 0
                        || connPtr == 0
                        || dataSize < Marshal.SizeOf<TopBarNativeMethods.WlanConnectionAttributes>())
                    {
                        continue;
                    }

                    var conn = Marshal.PtrToStructure<TopBarNativeMethods.WlanConnectionAttributes>(connPtr);
                    var assoc = conn.AssociationAttributes;
                    var ssidLen = (int)Math.Min(assoc.Dot11Ssid.SsidLength, 32u);
                    var ssid = ssidLen > 0 && assoc.Dot11Ssid.Ssid is { Length: >= 32 }
                        ? TopBarModuleRules.DecodeSsidBytes(
                            assoc.Dot11Ssid.Ssid.AsSpan(0, ssidLen),
                            TopBarNativeMethods.DecodeSystemAnsi)
                        : null;
                    var quality = (int)Math.Clamp(assoc.WlanSignalQuality, 0u, 100u);
                    return (ssid, quality);
                }
                finally
                {
                    if (connPtr != 0)
                    {
                        TopBarNativeMethods.WlanFreeMemory(connPtr);
                    }
                }
            }

            return (null, null);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or AccessViolationException)
        {
            return (null, null);
        }
        finally
        {
            if (listPtr != 0)
            {
                TopBarNativeMethods.WlanFreeMemory(listPtr);
            }
            if (clientHandle != 0)
            {
                _ = TopBarNativeMethods.WlanCloseHandle(clientHandle, 0);
            }
        }
    }

    /// <summary>
    /// The taskbar-style badge of the foreground window's input method plus the layout
    /// handle (change detector for the slower name reads), the layout's full display
    /// name and whether the IME answered the mode question. The badge re-reads cheaply
    /// enough for the 150 ms focus cadence: the IME mode is one bounded cross-process
    /// call, and a busy IME (the user is typing) can miss the timeout.
    /// </summary>
    internal static (nint Layout, string Label, string FullName, bool ModeKnown) ReadInputMethod()
    {
        // Prefer the actual focused child window from GetGUIThreadInfo so UWP /
        // ApplicationFrameHost containers report the hosted input thread rather than the frame.
        var foreground = DockNativeMethods.GetForegroundWindow();
        var targetWindow = ResolveFocusedInputWindow(foreground);
        var threadId = targetWindow == 0
            ? 0u
            : DockNativeMethods.GetWindowThreadProcessId(targetWindow, out _);
        if (threadId == 0 && foreground != 0)
        {
            threadId = DockNativeMethods.GetWindowThreadProcessId(foreground, out _);
        }

        // No classic foreground window (a console host, for example) falls back to this
        // thread's own layout, which is the same list Windows would show.
        var layout = TopBarNativeMethods.GetKeyboardLayout(threadId);
        if (layout == 0)
        {
            layout = TopBarNativeMethods.GetKeyboardLayout(0);
        }

        var conversionMode = ReadConversionMode(targetWindow != 0 ? targetWindow : foreground);
        return (layout,
            TopBarModuleRules.DescribeInputMethod(layout, conversionMode),
            LayoutName(layout),
            conversionMode is not null);
    }

    private static nint ResolveFocusedInputWindow(nint foreground)
    {
        if (foreground == 0)
        {
            return 0;
        }

        var gui = new TopBarNativeMethods.GuiThreadInfo
        {
            Size = Marshal.SizeOf<TopBarNativeMethods.GuiThreadInfo>()
        };
        if (TopBarNativeMethods.GetGUIThreadInfo(0, ref gui) && gui.HwndFocus != 0)
        {
            return gui.HwndFocus;
        }
        return foreground;
    }

    /// <summary>
    /// The active input processor's own name ("微软拼音", "微信输入法"), or null when a
    /// plain keyboard layout is active. TSF answers only on an STA thread, so this rides
    /// the UI-thread cadence, not the background system-info read.
    /// </summary>
    internal static string? ReadInputMethodName() =>
        TsfInputProcessorInterop.ReadActiveInputProcessorName();

    internal static TsfInputProcessorInterop.ActiveProfileSnapshot ReadActiveInputMethod() =>
        TsfInputProcessorInterop.ReadActiveInputProcessor();

    internal static IReadOnlyList<TopBarInputMethodOption> ReadInputMethodOptions() =>
        TsfInputProcessorInterop.EnumerateEnabledProfiles(LayoutName);

    internal static bool ActivateInputMethod(TopBarInputMethodOption option, nint foregroundWindow) =>
        TsfInputProcessorInterop.ActivateInputMethod(option, foregroundWindow);

    internal static IDisposable? SubscribeInputMethodChanges(Action onChanged) =>
        TsfInputProcessorInterop.SubscribeProfileActivation(onChanged);

    private static int? ReadConversionMode(nint foreground)
    {
        if (foreground == 0)
        {
            return null;
        }

        var imeWindow = TopBarNativeMethods.ImmGetDefaultIMEWnd(foreground);
        if (imeWindow == 0)
        {
            return null;
        }

        // The IME window belongs to the foreground window's thread, so the call crosses
        // a process boundary: bound it and give up on a hung target instead of stalling
        // the caller.
        if (!TopBarNativeMethods.SendMessageTimeout(
                imeWindow,
                TopBarNativeMethods.WmImeControl,
                TopBarNativeMethods.ImcGetConversionMode,
                0,
                TopBarNativeMethods.SmtoAbortIfHung,
                ConversionModeTimeoutMilliseconds,
                out var mode))
        {
            return null;
        }

        return unchecked((int)mode);
    }

    private const uint ConversionModeTimeoutMilliseconds = 50;
    private static readonly ConcurrentDictionary<nint, string> LayoutNames = new();

    private static string LayoutName(nint layout)
    {
        if (layout == 0)
        {
            return LayoutFallback;
        }

        return LayoutNames.GetOrAdd(layout, static handle =>
        {
            foreach (var id in TopBarModuleRules.FormatLayoutRegistryCandidates(handle))
            {
                var text = Registry.GetValue(
                    $"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet\\Control\\Keyboard Layouts\\{id}",
                    "Layout Text",
                    null) as string;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return LayoutFallback;
        });
    }

    private const string LayoutFallback = "键盘";
}
