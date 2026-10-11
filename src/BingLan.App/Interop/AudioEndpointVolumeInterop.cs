using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

/// <summary>
/// Read-only master volume through the documented WASAPI COM interfaces. Only the
/// enumerator and the scalar query are brought in: the bar shows the level, it never
/// changes it.
/// </summary>
internal static class AudioEndpointVolumeInterop
{
    // The MMDeviceEnumerator coclass and its interface; the interface needs its own
    // IID, since casting the RCW queries for exactly that.
    private const string EnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    private const string EnumeratorInterfaceId = "A95664D2-9614-4F35-A746-DE8DB63617E6";
    private const string DeviceInterfaceId = "D666063F-1587-4E43-81F1-B948E807363F";
    private const string EndpointInterfaceId = "5CDF2C82-841E-4546-9722-0CF74078229A";
    private const int ClsctxInprocServer = 0x1;
    private const int Render = 0;
    private const int ConsoleRole = 1;

    /// <summary>
    /// The master volume of the default audio endpoint, 0–1, or null when audio is not
    /// available (no device, service down, COM failure).
    /// </summary>
    internal static float? ReadMasterVolumeScalar()
    {
        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid(EnumeratorClsid));
            if (enumeratorType is null)
            {
                return null;
            }

            var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            try
            {
                if (enumerator.GetDefaultAudioEndpoint(Render, ConsoleRole, out var device) != 0
                    || device is null)
                {
                    return null;
                }

                try
                {
                    var endpointId = new Guid(EndpointInterfaceId);
                    if (device.Activate(ref endpointId, ClsctxInprocServer, 0, out var endpoint) != 0
                        || endpoint is null)
                    {
                        return null;
                    }

                    try
                    {
                        return endpoint.GetMasterVolumeLevelScalar(out var scalar) == 0
                            ? scalar
                            : null;
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
        catch (COMException)
        {
            return null;
        }
        catch (InvalidComObjectException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Interfaces are declared in the exact order of the real vtables: COM interop
    // places each method by its position here, so a shortened interface would call
    // the wrong slot.
    [ComImport, Guid(EnumeratorInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint collection);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(nint callback);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(nint callback);
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
            out IAudioEndpointVolume? endpoint);

        [PreserveSig]
        int OpenPropertyStore(int access, out nint store);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
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
        int SetMute(bool mute, ref Guid context);

        [PreserveSig]
        int GetMute(out bool mute);

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
}
