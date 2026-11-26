using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

/// <summary>One playable render endpoint as the bar shows it.</summary>
internal sealed record AudioRenderDevice(string Id, string Name, bool IsDefault);

/// <summary>
/// Enumerates the active render endpoints and switches the system default one. Reading
/// and switching volume stays in <see cref="AudioEndpointVolumeInterop"/>; this type only
/// answers "which devices exist" and "make that one the default".
///
/// The switch itself has no documented API: <c>IPolicyConfig::SetDefaultEndpoint</c> is the
/// long-standing undocumented entry point every Windows volume switcher uses. It is called
/// defensively — any failure reports false so the caller can fall back to the system sound
/// settings instead of silently doing nothing.
/// </summary>
internal static class AudioRenderDeviceInterop
{
    private const string EnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    private const string EnumeratorInterfaceId = "A95664D2-9614-4F35-A746-DE8DB63617E6";
    private const string DeviceCollectionInterfaceId = "0BD7A1BE-7A1A-44DB-8397-CC5392387B5E";
    private const string DeviceInterfaceId = "D666063F-1587-4E43-81F1-B948E807363F";
    private const string PropertyStoreInterfaceId = "886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99";
    private const string PolicyConfigClientClsid = "870AF99C-171D-4F9E-AF0D-E63DF40C2BC9";
    private const string PolicyConfigInterfaceId = "F8679F50-850A-41CF-9C72-430F290290C8";

    private const int ClsctxInprocServer = 0x1;
    private const int Render = 0;
    private const int ConsoleRole = 1;
    private const int DeviceStateActive = 0x1;
    private const int StgmRead = 0;

    // PKEY_Device_FriendlyName
    private static readonly PropertyKey FriendlyNameKey = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);

    /// <summary>
    /// The active render endpoints, current default first. Returns an empty list when audio
    /// is unavailable, so the panel can show "no device" instead of throwing.
    /// </summary>
    internal static IReadOnlyList<AudioRenderDevice> EnumerateRenderDevices()
    {
        var result = new List<AudioRenderDevice>();
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid(EnumeratorClsid));
            if (enumeratorType is null)
            {
                return result;
            }

            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            try
            {
                var defaultId = ReadDefaultDeviceId(enumerator);
                // IMMDeviceCollection is declared with its real vtable so Item() lands on
                // slot 4; a placeholder interface would call into the wrong method.
                if (enumerator.EnumAudioEndpoints(Render, DeviceStateActive, out var collection) != 0
                    || collection is null)
                {
                    return result;
                }

                try
                {
                    if (collection.GetCount(out var count) != 0)
                    {
                        return result;
                    }

                    for (var index = 0; index < count; index++)
                    {
                        if (collection.Item(index, out var device) != 0 || device is null)
                        {
                            continue;
                        }

                        try
                        {
                            if (device.GetId(out var id) != 0 || string.IsNullOrEmpty(id))
                            {
                                continue;
                            }

                            var name = ReadFriendlyName(device) ?? id;
                            result.Add(new AudioRenderDevice(
                                id,
                                name,
                                string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
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
            }
            finally
            {
                Marshal.ReleaseComObject(enumerator);
            }
        }
        catch (COMException)
        {
        }
        catch (InvalidCastException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        // The default goes first so the list reads like the panel: current, then the rest.
        return result
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Makes the endpoint the default playback device for every role. False means the
    /// undocumented call was refused — the caller then falls back to the system settings.
    /// </summary>
    internal static bool SetDefaultRenderDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return false;
        }

        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(PolicyConfigClientClsid));
            if (type is null)
            {
                return false;
            }

            var policy = (IPolicyConfig)Activator.CreateInstance(type)!;
            try
            {
                // eConsole, eMultimedia, eCommunications — all three so the change is global.
                return policy.SetDefaultEndpoint(deviceId, 0) == 0
                    && policy.SetDefaultEndpoint(deviceId, 1) == 0
                    && policy.SetDefaultEndpoint(deviceId, 2) == 0;
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string? ReadDefaultDeviceId(IMMDeviceEnumerator enumerator)
    {
        if (enumerator.GetDefaultAudioEndpoint(Render, ConsoleRole, out var device) != 0
            || device is null)
        {
            return null;
        }

        try
        {
            return device.GetId(out var id) == 0 ? id : null;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(StgmRead, out var storePointer) != 0 || storePointer == 0)
        {
            return null;
        }

        var store = (IPropertyStore)Marshal.GetObjectForIUnknown(storePointer);
        try
        {
            var key = FriendlyNameKey;
            if (store.GetValue(ref key, out var value) != 0)
            {
                return null;
            }

            try
            {
                // VT_LPWSTR is the only shape PKEY_Device_FriendlyName comes in here.
                return value.VarType == 31 && value.PointerValue != 0
                    ? Marshal.PtrToStringUni(value.PointerValue)
                    : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
            // GetObjectForIUnknown took its own reference; the property-store pointer from
            // OpenPropertyStore is owned by the device and must not be released here.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, int propertyId)
    {
        internal readonly Guid FormatId = formatId;
        internal readonly int PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] internal ushort VarType;
        [FieldOffset(8)] internal nint PointerValue;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant value);

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
        int Activate(ref Guid interfaceId, int classContext, nint activationParams, out nint instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out nint store);

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

    // Slots 1–10 are unused by this app but must be declared to reach SetDefaultEndpoint
    // at its real vtable position.
    [ComImport, Guid(PolicyConfigInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig]
        int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out nint format);

        [PreserveSig]
        int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out nint format);

        [PreserveSig]
        int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint endPointFormat, nint mixFormat);

        [PreserveSig]
        int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int isDefault, out long defaultPeriod, out long minimumPeriod);

        [PreserveSig]
        int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ref long period);

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
