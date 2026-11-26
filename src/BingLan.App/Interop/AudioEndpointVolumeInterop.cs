using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using BingLan.App.Dock;
using BingLan.Core.Dock;
using BingLan.Core.TopBar;

namespace BingLan.App.Interop;

/// <summary>
/// Master volume, output-device enumeration/switching and per-app audio session controls
/// through Windows WASAPI COM interfaces. No elevated privileges or external packages required.
/// </summary>
internal static class AudioEndpointVolumeInterop
{
    private const string EnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    private const string EnumeratorInterfaceId = "A95664D2-9614-4F35-A746-DE8DB63617E6";
    private const string DeviceCollectionInterfaceId = "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E";
    private const string DeviceInterfaceId = "D666063F-1587-4E43-81F1-B948E807363F";
    private const string PropertyStoreInterfaceId = "886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99";
    private const string EndpointInterfaceId = "5CDF2C82-841E-4546-9722-0CF74078229A";
    private const string SessionManager2InterfaceId = "77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F";
    private const string SessionEnumeratorInterfaceId = "E2F5BB11-0570-40CA-ACDD-3AA01277DEE8";
    private const string SessionControlInterfaceId = "F4B1A599-7266-4319-A8CA-E70ACB11E8CD";
    private const string SessionControl2InterfaceId = "BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D";
    private const string SimpleAudioVolumeInterfaceId = "87CE5498-68D6-44E5-9215-6DA47EF883D8";
    private const string PolicyConfigClsid = "870AF99C-171D-4F9E-AF0D-E63DF40C2BC9";
    private const string PolicyConfigInterfaceId = "F8679F50-850A-41CF-9C72-430F290290C8";

    private const int ClsctxInprocServer = 0x1;
    private const int ClsctxAll = 0x17;
    private const int Render = 0;
    private const int ConsoleRole = 0;
    private const int MultimediaRole = 1;
    private const int CommunicationsRole = 2;
    private const int DeviceStateActive = 0x1;
    private const int StgmRead = 0;
    private const int AudioSessionStateActive = 1;
    private const int AudioSessionStateExpired = 2;

    // PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, pid = 14
    private static readonly PropertyKey DeviceFriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    internal static (float Scalar, bool Muted)? ReadMasterVolume()
    {
        return WithDefaultEndpoint(endpoint =>
        {
            if (endpoint.GetMasterVolumeLevelScalar(out var scalar) != 0)
            {
                return ((float Scalar, bool Muted)?)null;
            }
            var muted = endpoint.GetMute(out var isMuted) == 0 && isMuted;
            return (scalar, muted);
        });
    }

    internal static bool SetMasterVolume(float scalar)
    {
        var clamped = Math.Clamp(scalar, 0f, 1f);
        return WithDefaultEndpoint(endpoint =>
        {
            var context = Guid.Empty;
            var ok = endpoint.SetMasterVolumeLevelScalar(clamped, ref context) == 0;
            if (ok && clamped > 0.001f && endpoint.GetMute(out var isMuted) == 0 && isMuted)
            {
                _ = TrySetMuteOnEndpoint(endpoint, false);
            }
            return ok;
        });
    }

    internal static bool SetMasterMute(bool muted)
    {
        return WithDefaultEndpoint(endpoint => TrySetMuteOnEndpoint(endpoint, muted));
    }

    private static bool TrySetMuteOnEndpoint(IAudioEndpointVolume endpoint, bool muted)
    {
        // Some USB/headset drivers (e.g. HECATE/Realtek combos) reject a non-null event context
        // on SetMute; pass null pointer first, then fall back to VK_VOLUME_MUTE only when the
        // endpoint refuses AND GetMute confirms its current mute state differs from the target.
        if (endpoint.SetMute(muted, 0) == 0)
        {
            return true;
        }

        if (endpoint.GetMute(out var currentMuted) != 0)
        {
            return false;
        }

        if (currentMuted == muted)
        {
            return true;
        }

        if (!SendVolumeMuteKey())
        {
            return false;
        }

        // Wait briefly (serialized under the audio write gate) so a queued VK_VOLUME_MUTE
        // takes effect before any subsequent mute/unmute request reads GetMute.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            Thread.Sleep(25);
            if (endpoint.GetMute(out var afterMuted) == 0 && afterMuted == muted)
            {
                return true;
            }
        }
        return false;
    }

    private static bool SendVolumeMuteKey()
    {
        const uint InputKeyboard = 1;
        const ushort VkVolumeMute = 0xAD;
        const uint KeyEventFKeyUp = 0x0002;
        KeyboardInputPacket[] inputs =
        [
            new()
            {
                Type = InputKeyboard,
                Keyboard = new KeyboardInputData { VirtualKey = VkVolumeMute }
            },
            new()
            {
                Type = InputKeyboard,
                Keyboard = new KeyboardInputData { VirtualKey = VkVolumeMute, Flags = KeyEventFKeyUp }
            }
        ];
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyboardInputPacket>()) == inputs.Length;
    }

    internal static TopBarAudioSnapshot ReadAudioSnapshot()
    {
        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null)
            {
                return TopBarAudioSnapshot.Unavailable;
            }
            try
            {
                string? defaultId = null;
                var masterPercent = 0;
                var masterMuted = false;
                var available = false;
                var sessions = new List<TopBarAudioSession>();

                if (enumerator.GetDefaultAudioEndpoint(Render, MultimediaRole, out var defaultDevice) == 0
                    && defaultDevice is not null)
                {
                    try
                    {
                        if (defaultDevice.GetId(out var id) == 0)
                        {
                            defaultId = id;
                        }
                        var endpointId = new Guid(EndpointInterfaceId);
                        if (defaultDevice.Activate(ref endpointId, ClsctxInprocServer, 0, out var endpointObj) == 0
                            && endpointObj is IAudioEndpointVolume endpoint)
                        {
                            try
                            {
                                if (endpoint.GetMasterVolumeLevelScalar(out var scalar) == 0)
                                {
                                    masterPercent = TopBarModuleRules.VolumePercentFromScalar(scalar);
                                    masterMuted = endpoint.GetMute(out var isMuted) == 0 && isMuted;
                                    available = true;
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(endpoint);
                            }
                        }

                        CollectSessions(defaultDevice, sessions);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(defaultDevice);
                    }
                }

                var devices = EnumerateDevices(enumerator, defaultId);
                return new TopBarAudioSnapshot(
                    MasterPercent: masterPercent,
                    MasterMuted: masterMuted,
                    Available: available || devices.Count > 0,
                    Devices: devices,
                    Sessions: TopBarControlRules.NormalizeAudioSessions(sessions),
                    DefaultDeviceId: defaultId);
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException or InvalidOperationException)
        {
            return TopBarAudioSnapshot.Unavailable;
        }
    }

    internal static bool SetDefaultOutputDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            var policyType = Type.GetTypeFromCLSID(new Guid(PolicyConfigClsid));
            if (policyType is null)
            {
                return false;
            }
            var raw = Activator.CreateInstance(policyType);
            if (raw is not IPolicyConfig policyConfig)
            {
                if (raw is not null && Marshal.IsComObject(raw))
                {
                    Marshal.ReleaseComObject(raw);
                }
                return false;
            }

            try
            {
                // Only update Console + Multimedia roles (do not overwrite an independently configured Communications device),
                // and require MultimediaRole to succeed since the flyout reads and controls MultimediaRole.
                _ = policyConfig.SetDefaultEndpoint(deviceId, ConsoleRole);
                var multimediaResult = policyConfig.SetDefaultEndpoint(deviceId, MultimediaRole);
                return multimediaResult == 0;
            }
            finally
            {
                Marshal.ReleaseComObject(policyConfig);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException or InvalidOperationException)
        {
            return false;
        }
    }

    internal static bool SetSessionVolume(string? deviceId, uint processId, float scalar)
    {
        var clamped = Math.Clamp(scalar, 0f, 1f);
        return ModifyMatchingSessions(deviceId, processId, simple =>
        {
            var context = Guid.Empty;
            var ok = simple.SetMasterVolume(clamped, ref context) == 0;
            if (ok && clamped > 0.001f && simple.GetMute(out var muted) == 0 && muted)
            {
                _ = simple.SetMute(false, ref context);
            }
            return ok;
        });
    }

    internal static bool SetSessionMute(string? deviceId, uint processId, bool muted)
    {
        return ModifyMatchingSessions(deviceId, processId, simple =>
        {
            var context = Guid.Empty;
            return simple.SetMute(muted, ref context) == 0;
        });
    }

    private static bool ModifyMatchingSessions(string? deviceId, uint processId, Func<ISimpleAudioVolume, bool> action)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null)
            {
                return false;
            }
            try
            {
                IMMDevice? device = null;
                if (!string.IsNullOrWhiteSpace(deviceId))
                {
                    if (enumerator.GetDevice(deviceId, out device) != 0 || device is null)
                    {
                        return false;
                    }
                }
                else if (enumerator.GetDefaultAudioEndpoint(Render, MultimediaRole, out device) != 0 || device is null)
                {
                    return false;
                }
                try
                {
                    var managerId = new Guid(SessionManager2InterfaceId);
                    if (device.Activate(ref managerId, ClsctxAll, 0, out var managerObj) != 0
                        || managerObj is not IAudioSessionManager2 manager)
                    {
                        return false;
                    }
                    try
                    {
                        if (manager.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum is null)
                        {
                            return false;
                        }
                        try
                        {
                            if (sessionEnum.GetCount(out var count) != 0)
                            {
                                return false;
                            }
                            var updated = false;
                            for (var i = 0; i < count; i++)
                            {
                                if (sessionEnum.GetSession(i, out var control) != 0 || control is null)
                                {
                                    continue;
                                }
                                try
                                {
                                    if (control is IAudioSessionControl2 control2
                                        && control2.GetProcessId(out var pid) == 0
                                        && pid == processId
                                        && control is ISimpleAudioVolume simple)
                                    {
                                        updated |= action(simple);
                                    }
                                }
                                finally
                                {
                                    Marshal.ReleaseComObject(control);
                                }
                            }
                            return updated;
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(sessionEnum);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(manager);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<TopBarAudioDevice> EnumerateDevices(IMMDeviceEnumerator enumerator, string? defaultId)
    {
        var results = new List<TopBarAudioDevice>();
        if (enumerator.EnumAudioEndpoints(Render, DeviceStateActive, out var collection) != 0
            || collection is null)
        {
            return results;
        }

        try
        {
            if (collection.GetCount(out var count) != 0 || count <= 0)
            {
                return results;
            }
            for (var i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0 || device is null)
                {
                    continue;
                }
                try
                {
                    if (device.GetId(out var id) != 0 || string.IsNullOrWhiteSpace(id))
                    {
                        continue;
                    }
                    var name = ReadFriendlyName(device);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        name = "音频输出设备";
                    }
                    var isDefault = !string.IsNullOrWhiteSpace(defaultId)
                        && string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase);
                    results.Add(new TopBarAudioDevice(id, name, isDefault));
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }

        return results
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var store) != 0 || store is null)
        {
            return null;
        }
        try
        {
            var key = DeviceFriendlyNameKey;
            var hr = store.GetValue(ref key, out var value);
            if (hr < 0)
            {
                return null;
            }
            try
            {
                return value.VarType == 31 && value.Pointer != 0
                    ? Marshal.PtrToStringUni(value.Pointer)?.Trim()
                    : null;
            }
            finally
            {
                _ = PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void CollectSessions(IMMDevice defaultDevice, List<TopBarAudioSession> results)
    {
        var managerId = new Guid(SessionManager2InterfaceId);
        if (defaultDevice.Activate(ref managerId, ClsctxAll, 0, out var managerObj) != 0
            || managerObj is not IAudioSessionManager2 manager)
        {
            return;
        }

        try
        {
            if (manager.GetSessionEnumerator(out var sessionEnum) != 0 || sessionEnum is null)
            {
                return;
            }
            try
            {
                if (sessionEnum.GetCount(out var count) != 0)
                {
                    return;
                }
                for (var i = 0; i < count; i++)
                {
                    if (sessionEnum.GetSession(i, out var control) != 0 || control is null)
                    {
                        continue;
                    }
                    try
                    {
                        if (control.GetState(out var state) != 0 || state == AudioSessionStateExpired)
                        {
                            continue;
                        }
                        if (control is not IAudioSessionControl2 control2
                            || control2.IsSystemSoundsSession() == 0
                            || control2.GetProcessId(out var pid) != 0
                            || pid == 0)
                        {
                            continue;
                        }
                        if (control is not ISimpleAudioVolume simple
                            || simple.GetMasterVolume(out var scalar) != 0)
                        {
                            continue;
                        }
                        var muted = simple.GetMute(out var isMuted) == 0 && isMuted;
                        _ = control.GetDisplayName(out var sessionDisplay);
                        var displayName = ResolveSessionDisplayName(pid, sessionDisplay);
                        if (string.IsNullOrWhiteSpace(displayName))
                        {
                            continue;
                        }
                        results.Add(new TopBarAudioSession(
                            ProcessId: pid,
                            DisplayName: displayName,
                            VolumePercent: TopBarModuleRules.VolumePercentFromScalar(scalar),
                            IsMuted: muted,
                            IsActive: state == AudioSessionStateActive));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(control);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessionEnum);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }
    }

    private static string? ResolveSessionDisplayName(uint pid, string? rawSessionDisplay)
    {
        var exePath = WindowCatalog.ExecutablePathOf(pid);
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            var desc = DockAppResolver.GetFileDescription(exePath);
            if (!string.IsNullOrWhiteSpace(desc))
            {
                return desc;
            }
            var stem = Path.GetFileNameWithoutExtension(exePath);
            if (AppDisplayNameRules.Normalize(stem, exePath) is { } byExe)
            {
                return byExe;
            }
        }

        if (!string.IsNullOrWhiteSpace(rawSessionDisplay)
            && !rawSessionDisplay.StartsWith('@'))
        {
            return AppDisplayNameRules.Normalize(rawSessionDisplay, exePath);
        }

        try
        {
            using var proc = Process.GetProcessById((int)pid);
            return AppDisplayNameRules.Normalize(proc.ProcessName, exePath);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static T WithDefaultEndpoint<T>(Func<IAudioEndpointVolume, T> action)
    {
        try
        {
            var enumerator = CreateEnumerator();
            if (enumerator is null)
            {
                return default!;
            }
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(Render, MultimediaRole, out var device) != 0
                    || device is null)
                {
                    return default!;
                }
                try
                {
                    var endpointId = new Guid(EndpointInterfaceId);
                    if (device.Activate(ref endpointId, ClsctxInprocServer, 0, out var endpointObj) != 0
                        || endpointObj is not IAudioEndpointVolume endpoint)
                    {
                        return default!;
                    }
                    try
                    {
                        return action(endpoint);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(endpoint);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidComObjectException or InvalidCastException or InvalidOperationException)
        {
            return default!;
        }
    }

    private static IMMDeviceEnumerator? CreateEnumerator()
    {
        var enumeratorType = Type.GetTypeFromCLSID(new Guid(EnumeratorClsid));
        return enumeratorType is null
            ? null
            : Activator.CreateInstance(enumeratorType) as IMMDeviceEnumerator;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, KeyboardInputPacket[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputPacket
    {
        internal uint Type;
        internal KeyboardInputData Keyboard;
        internal long Padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid fmtid, uint pid)
    {
        internal Guid Fmtid = fmtid;
        internal uint Pid = pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] internal ushort VarType;
        [FieldOffset(8)] internal nint Pointer;
        [FieldOffset(16)] internal long Reserved;
    }

    [ComImport, Guid(EnumeratorInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? collection);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint callback);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint callback);
    }

    [ComImport, Guid(DeviceCollectionInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int Item(int index, out IMMDevice? device);
    }

    [ComImport, Guid(DeviceInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid interfaceId,
            int classContext,
            nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? activated);

        [PreserveSig]
        int OpenPropertyStore(int access, out IPropertyStore? store);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport, Guid(PropertyStoreInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PropertyKey key);

        [PreserveSig]
        int GetValue(ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetValue(ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int Commit();
    }

    [ComImport, Guid(EndpointInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig]
        int RegisterControlChangeNotify(nint callback);

        [PreserveSig]
        int UnregisterControlChangeNotify(nint callback);

        [PreserveSig]
        int GetChannelCount(out int channelCount);

        [PreserveSig]
        int SetMasterVolumeLevel(float level, ref Guid context);

        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid context);

        [PreserveSig]
        int GetMasterVolumeLevel(out float level);

        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);

        [PreserveSig]
        int SetChannelVolumeLevel(int channel, float level, ref Guid context);

        [PreserveSig]
        int SetChannelVolumeLevelScalar(int channel, float level, ref Guid context);

        [PreserveSig]
        int GetChannelVolumeLevel(int channel, out float level);

        [PreserveSig]
        int GetChannelVolumeLevelScalar(int channel, out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, nint context);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        [PreserveSig]
        int GetVolumeStepInfo(out int step, out int stepCount);

        [PreserveSig]
        int VolumeStepUp(ref Guid context);

        [PreserveSig]
        int VolumeStepDown(ref Guid context);

        [PreserveSig]
        int QueryHardwareSupport(out int supportMask);

        [PreserveSig]
        int GetVolumeRange(out float minLevelDecibels, out float maxLevelDecibels, out float incrementDecibels);
    }

    [ComImport, Guid(SessionManager2InterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(nint audioSessionGuid, uint streamFlags, out nint sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags, out nint audioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator? sessionEnum);
    }

    [ComImport, Guid(SessionEnumeratorInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int count);

        [PreserveSig]
        int GetSession(int sessionIndex, out IAudioSessionControl? session);
    }

    [ComImport, Guid(SessionControlInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        int SetGroupingParam(ref Guid overrideValue, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint client);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint client);
    }

    [ComImport, Guid(SessionControl2InterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out int state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        int SetGroupingParam(ref Guid overrideValue, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(nint client);

        [PreserveSig]
        int UnregisterAudioSessionNotification(nint client);

        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetProcessId(out uint processId);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid(SimpleAudioVolumeInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid(PolicyConfigInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out nint format);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultFormat, out nint format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint endpointFormat, nint mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int defaultPeriod, out long defaultDevicePeriod, out long minDevicePeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long devicePeriod);

        [PreserveSig]
        int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out nint mode);

        [PreserveSig]
        int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint mode);

        [PreserveSig]
        int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, out PropVariant value);

        [PreserveSig]
        int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref PropertyKey key, ref PropVariant value);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);

        [PreserveSig]
        int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int visible);
    }
}
