using System.Runtime.InteropServices;
using System.Diagnostics;

using BingLan.Core.Audio;

namespace BingLan.App.Audio;

/// <summary>
/// Zero-dependency WASAPI Loopback capture over documented Windows Core Audio COM interfaces.
/// Captures default render endpoint audio on a dedicated background thread, handles device changes,
/// and throttles wait intervals automatically while the system is silent.
/// </summary>
internal sealed class WasapiLoopbackCapture : IDisposable
{
    private const string EnumeratorClsid = "BCDE0395-E52F-467C-8E3D-C4579291692E";
    private const string EnumeratorInterfaceId = "A95664D2-9614-4F35-A746-DE8DB63617E6";
    private const string NotificationClientInterfaceId = "7991EEC9-7E89-4D85-8390-6C703CEC60C0";
    private const string DeviceInterfaceId = "D666063F-1587-4E43-81F1-B948E807363F";
    private const string AudioClientInterfaceId = "1CB9AD4C-DBFA-4C32-B178-C2F568A703B2";
    private const string AudioCaptureClientInterfaceId = "C8ADBD64-E71E-48A0-A4DE-185C395CD317";

    private static readonly Guid SubTypeIeeeFloat = new("00000003-0000-0010-8000-00AA00389B71");
    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00AA00389B71");

    private const int ClsctxAll = 0x17;
    private const int DataFlowRender = 0;
    private const int RoleMultimedia = 1;
    private const int ShareModeShared = 0;
    private const uint StreamFlagLoopback = 0x00020000;
    private const uint StreamFlagEventCallback = 0x00040000;
    private const uint BufferFlagSilent = 0x2;
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);
    private const ushort WaveFormatPcm = 1;
    private const ushort WaveFormatIeeeFloat = 3;
    private const ushort WaveFormatExtensible = 0xFFFE;

    private readonly SpectrumAnalyzer _analyzer;
    private readonly Action<float[]> _onFrame;
    private readonly float[] _levels;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private readonly object _lifetimeGate = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ManualResetEventSlim _stopEvent = new(false);
    private readonly AutoResetEvent _deviceChangeEvent = new(false);
    private Thread? _workerThread;
    private volatile bool _disposed;

    public WasapiLoopbackCapture(SpectrumAnalyzer analyzer, Action<float[]> onFrame)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _onFrame = onFrame;
        _levels = new float[analyzer.BarCount];
    }

    public Task Completion => _completion.Task;

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int BitsPerSample { get; private set; }
    public bool IsEventDriven { get; private set; }
    public bool IsCapturing { get; private set; }
    public long PacketsRead => Interlocked.Read(ref _packetsRead);
    public long NonSilentPackets => Interlocked.Read(ref _nonSilentPackets);
    public long ReconnectCount => Interlocked.Read(ref _reconnectCount);
    public string? LastError { get; private set; }

    private long _packetsRead;
    private long _nonSilentPackets;
    private long _reconnectCount;

    public void Start()
    {
        if (_workerThread is not null)
        {
            return;
        }

        _workerThread = new Thread(CaptureLoop)
        {
            Name = "BingLan.WasapiLoopback",
            IsBackground = true
        };
        _workerThread.SetApartmentState(ApartmentState.MTA);
        _workerThread.Start();
    }

    public void Dispose()
    {
        // The capture thread owns all handles and COM releases. Never wait for a driver on the UI thread.
        lock (_lifetimeGate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _stopEvent.Set();
        }
    }

    private void CaptureLoop()
    {
        IMMDeviceEnumerator? enumerator = null;
        DeviceNotificationClient? notifier = null;
        var sampleScratch = new float[4096];

        try
        {
            var enumeratorType = Type.GetTypeFromCLSID(new Guid(EnumeratorClsid));
            if (enumeratorType is null)
            {
                LastError = "MMDeviceEnumerator CLSID unavailable.";
                return;
            }

            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
            notifier = new DeviceNotificationClient(SignalDeviceChange);
            _ = enumerator.RegisterEndpointNotificationCallback(notifier);

            var retryDelay = 250;
            while (!_stopEvent.IsSet)
            {
                int hr;
                try
                {
                    hr = RunSingleSession(enumerator, ref sampleScratch);
                }
                catch (COMException ex)
                {
                    hr = ex.HResult;
                }
                IsCapturing = false;
                if (_stopEvent.IsSet)
                {
                    break;
                }

                Interlocked.Increment(ref _reconnectCount);
                if (hr != 0 && hr != AudclntEDeviceInvalidated)
                {
                    LastError = $"WASAPI session ended with 0x{hr:X8}";
                }

                PublishFrame();
                // Missing or unsupported devices retry at most once per five seconds after backoff.
                WaitForRetry(retryDelay);
                retryDelay = hr == AudclntEDeviceInvalidated ? 250 : Math.Min(retryDelay * 2, 5000);
            }
        }
        catch (Exception ex)
        {
            LastError = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsCapturing = false;
            if (enumerator is not null)
            {
                if (notifier is not null)
                {
                    try
                    {
                        _ = enumerator.UnregisterEndpointNotificationCallback(notifier);
                    }
                    catch
                    {
                        // Best-effort cleanup during teardown.
                    }
                }

                Marshal.ReleaseComObject(enumerator);
            }

            lock (_lifetimeGate)
            {
                _disposed = true;
                _deviceChangeEvent.Dispose();
                _stopEvent.Dispose();
            }
            _completion.TrySetResult();
        }
    }

    private void SignalDeviceChange()
    {
        lock (_lifetimeGate)
        {
            if (!_disposed)
            {
                _deviceChangeEvent.Set();
            }
        }
    }

    private void WaitForRetry(int milliseconds)
    {
        WaitHandle[] handles = [_stopEvent.WaitHandle, _deviceChangeEvent];
        var deadline = Environment.TickCount64 + milliseconds;
        while (!_stopEvent.IsSet && Environment.TickCount64 < deadline)
        {
            var remaining = (int)Math.Min(int.MaxValue, deadline - Environment.TickCount64);
            var timeout = Math.Max(0, Math.Min(remaining, _analyzer.IsSilent ? 250 : 33));
            if (WaitHandle.WaitAny(handles, timeout) != WaitHandle.WaitTimeout)
            {
                return;
            }
            PublishFrame();
        }
    }

    private void PublishFrame()
    {
        var seconds = _frameClock.Elapsed.TotalSeconds;
        if (seconds < 1.0 / 30.0)
        {
            return;
        }
        _frameClock.Restart();
        if (_analyzer.ComputeFrame(seconds, _levels))
        {
            _onFrame(_levels);
        }
    }

    private unsafe int RunSingleSession(IMMDeviceEnumerator enumerator, ref float[] sampleScratch)
    {
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioCaptureClient? captureClient = null;
        nint mixFormatPtr = 0;
        nint audioEventHandle = 0;
        var started = false;

        try
        {
            _deviceChangeEvent.Reset();
            var hr = enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleMultimedia, out device);
            if (hr != 0 || device is null)
            {
                LastError = $"GetDefaultAudioEndpoint failed: 0x{hr:X8}";
                return hr == 0 ? -1 : hr;
            }

            var audioClientIid = new Guid(AudioClientInterfaceId);
            hr = device.Activate(ref audioClientIid, ClsctxAll, 0, out var audioClientObj);
            if (hr != 0 || audioClientObj is not IAudioClient client)
            {
                LastError = $"Activate(IAudioClient) failed: 0x{hr:X8}";
                return hr == 0 ? -1 : hr;
            }

            audioClient = client;
            hr = audioClient.GetMixFormat(out mixFormatPtr);
            if (hr != 0 || mixFormatPtr == 0)
            {
                LastError = $"GetMixFormat failed: 0x{hr:X8}";
                return hr == 0 ? -1 : hr;
            }

            if (!TryParseFormat(mixFormatPtr, out var format))
            {
                LastError = "Unsupported WASAPI mix format.";
                return -1;
            }

            SampleRate = format.SampleRate;
            Channels = format.Channels;
            BitsPerSample = format.BitsPerSample;
            _analyzer.PushSilence(_analyzer.FftSize, format.SampleRate, format.Channels);

            // 50 ms shared-mode buffer (units are 100 ns).
            const long bufferDuration100Ns = 500_000;
            audioEventHandle = CreateEventW(0, false, false, null);
            var eventDriven = false;

            if (audioEventHandle != 0)
            {
                hr = audioClient.Initialize(
                    ShareModeShared,
                    StreamFlagLoopback | StreamFlagEventCallback,
                    bufferDuration100Ns,
                    0,
                    mixFormatPtr,
                    0);
                if (hr == 0 && audioClient.SetEventHandle(audioEventHandle) == 0)
                {
                    eventDriven = true;
                }
            }

            if (!eventDriven)
            {
                // Some drivers reject EVENTCALLBACK on loopback; recreate IAudioClient and fall back cleanly.
                Marshal.ReleaseComObject(audioClient);
                audioClient = null;
                hr = device.Activate(ref audioClientIid, ClsctxAll, 0, out var fallbackObj);
                if (hr != 0 || fallbackObj is not IAudioClient fallbackClient)
                {
                    return hr == 0 ? -1 : hr;
                }

                audioClient = fallbackClient;
                hr = audioClient.Initialize(
                    ShareModeShared,
                    StreamFlagLoopback,
                    bufferDuration100Ns,
                    0,
                    mixFormatPtr,
                    0);
                if (hr != 0)
                {
                    LastError = $"IAudioClient.Initialize failed: 0x{hr:X8}";
                    return hr;
                }
            }

            IsEventDriven = eventDriven;

            var captureIid = new Guid(AudioCaptureClientInterfaceId);
            hr = audioClient.GetService(ref captureIid, out var captureObj);
            if (hr != 0 || captureObj is not IAudioCaptureClient cap)
            {
                LastError = $"GetService(IAudioCaptureClient) failed: 0x{hr:X8}";
                return hr == 0 ? -1 : hr;
            }

            captureClient = cap;
            hr = audioClient.Start();
            if (hr != 0)
            {
                LastError = $"IAudioClient.Start failed: 0x{hr:X8}";
                return hr;
            }

            started = true;
            IsCapturing = true;
            LastError = null;

            var stopNativeHandle = _stopEvent.WaitHandle.SafeWaitHandle.DangerousGetHandle();
            var deviceChangeNativeHandle = _deviceChangeEvent.SafeWaitHandle.DangerousGetHandle();
            var handles = eventDriven
                ? new[] { stopNativeHandle, deviceChangeNativeHandle, audioEventHandle }
                : new[] { stopNativeHandle, deviceChangeNativeHandle };

            var silentConsecutiveWaits = 0;

            while (!_stopEvent.IsSet)
            {
                // Idle streams use longer timeout checks; audio and device notifications still wake immediately.
                var waitMs = eventDriven
                    ? (silentConsecutiveWaits > 20 ? 150u : 33u)
                    : (silentConsecutiveWaits > 75 ? 100u : 16u);

                var waitResult = WaitForMultipleObjects((uint)handles.Length, handles, false, waitMs);
                if (waitResult == 0) // stopEvent
                {
                    return 0;
                }

                if (waitResult == 1) // default device changed
                {
                    return AudclntEDeviceInvalidated;
                }
                if (waitResult == uint.MaxValue)
                {
                    return Marshal.GetHRForLastWin32Error();
                }

                var drainedAny = false;
                while (!_stopEvent.IsSet)
                {
                    hr = captureClient.GetNextPacketSize(out var packetFrames);
                    if (hr != 0)
                    {
                        return hr;
                    }

                    if (packetFrames == 0)
                    {
                        break;
                    }

                    hr = captureClient.GetBuffer(
                        out var dataPtr,
                        out var numFrames,
                        out var flags,
                        out _,
                        out _);
                    if (hr != 0)
                    {
                        return hr;
                    }

                    try
                    {
                        Interlocked.Increment(ref _packetsRead);
                        if (numFrames > 0)
                        {
                            if ((flags & BufferFlagSilent) != 0 || dataPtr == 0)
                            {
                                _analyzer.PushSilence((int)numFrames, format.SampleRate, format.Channels);
                            }
                            else
                            {
                                drainedAny = true;
                                Interlocked.Increment(ref _nonSilentPackets);
                                var sampleCount = checked((int)numFrames * format.Channels);
                                if (sampleScratch.Length < sampleCount)
                                {
                                    sampleScratch = new float[sampleCount];
                                }

                                DecodeInterleavedSamples(dataPtr, sampleCount, format, sampleScratch);
                                _analyzer.PushInterleavedSamples(sampleScratch.AsSpan(0, sampleCount), format.Channels, format.SampleRate);
                            }
                        }
                    }
                    finally
                    {
                        _ = captureClient.ReleaseBuffer(numFrames);
                    }
                }

                if (drainedAny)
                {
                    silentConsecutiveWaits = 0;
                }
                else if (silentConsecutiveWaits < 1000)
                {
                    silentConsecutiveWaits++;
                }
                PublishFrame();
            }

            return 0;
        }
        finally
        {
            if (started && audioClient is not null)
            {
                try
                {
                    _ = audioClient.Stop();
                }
                catch
                {
                    // Ignore teardown errors when device already disappeared.
                }
            }

            if (captureClient is not null)
            {
                Marshal.ReleaseComObject(captureClient);
            }

            if (audioClient is not null)
            {
                Marshal.ReleaseComObject(audioClient);
            }

            if (device is not null)
            {
                Marshal.ReleaseComObject(device);
            }

            if (mixFormatPtr != 0)
            {
                Marshal.FreeCoTaskMem(mixFormatPtr);
            }

            if (audioEventHandle != 0)
            {
                CloseHandle(audioEventHandle);
            }
        }
    }

    private static unsafe bool TryParseFormat(nint ptr, out AudioFormat format)
    {
        var wfx = *(WaveFormatEx*)ptr;
        var isFloat = false;
        var isPcm = false;

        if (wfx.FormatTag == WaveFormatIeeeFloat && wfx.BitsPerSample == 32)
        {
            isFloat = true;
        }
        else if (wfx.FormatTag == WaveFormatPcm && (wfx.BitsPerSample is 16 or 24 or 32))
        {
            isPcm = true;
        }
        else if (wfx.FormatTag == WaveFormatExtensible && wfx.ExtraSize >= 22)
        {
            var ext = *(WaveFormatExtensibleStruct*)ptr;
            if (ext.SubFormat == SubTypeIeeeFloat && wfx.BitsPerSample == 32)
            {
                isFloat = true;
            }
            else if (ext.SubFormat == SubTypePcm && (wfx.BitsPerSample is 16 or 24 or 32))
            {
                isPcm = true;
            }
        }

        if ((!isFloat && !isPcm) || wfx.Channels is 0 or > 32
            || wfx.SamplesPerSec is < 8000 or > 192000
            || wfx.BlockAlign != wfx.Channels * (wfx.BitsPerSample / 8))
        {
            format = default;
            return false;
        }

        format = new AudioFormat((int)wfx.SamplesPerSec, wfx.Channels, wfx.BitsPerSample, isFloat);
        return true;
    }

    private static unsafe void DecodeInterleavedSamples(nint dataPtr, int sampleCount, AudioFormat format, float[] destination)
    {
        if (format.IsFloat)
        {
            new ReadOnlySpan<float>((void*)dataPtr, sampleCount).CopyTo(destination);
        }
        else if (format.BitsPerSample == 16)
        {
            var src = (short*)dataPtr;
            const float scale = 1f / 32768f;
            for (var i = 0; i < sampleCount; i++)
            {
                destination[i] = src[i] * scale;
            }
        }
        else if (format.BitsPerSample == 24)
        {
            var src = (byte*)dataPtr;
            for (var i = 0; i < sampleCount; i++)
            {
                var offset = i * 3;
                var sample = (src[offset] | src[offset + 1] << 8 | src[offset + 2] << 16) << 8 >> 8;
                destination[i] = sample / 8388608f;
            }
        }
        else
        {
            var src = (int*)dataPtr;
            const float scale = 1f / 2147483648f;
            for (var i = 0; i < sampleCount; i++)
            {
                destination[i] = src[i] * scale;
            }
        }
    }

    private readonly record struct AudioFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatEx
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSec;
        public uint AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormatExtensibleStruct
    {
        public WaveFormatEx Format;
        public ushort ValidBitsPerSample;
        public uint ChannelMask;
        public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

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
        int RegisterEndpointNotificationCallback(IMMNotificationClient client);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid(NotificationClientInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMNotificationClient
    {
        [PreserveSig]
        int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);

        [PreserveSig]
        int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);

        [PreserveSig]
        int OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);

        [PreserveSig]
        int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PropertyKey key);
    }

    [ComImport, Guid(DeviceInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(
            ref Guid iid,
            int clsCtx,
            nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePointer);

        [PreserveSig]
        int OpenPropertyStore(int access, out nint properties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out uint state);
    }

    [ComImport, Guid(AudioClientInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioClient
    {
        [PreserveSig]
        int Initialize(
            int shareMode,
            uint streamFlags,
            long hnsBufferDuration,
            long hnsPeriodicity,
            nint pFormat,
            nint audioSessionGuid);

        [PreserveSig]
        int GetBufferSize(out uint numBufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long hnsLatency);

        [PreserveSig]
        int GetCurrentPadding(out uint numPaddingFrames);

        [PreserveSig]
        int IsFormatSupported(int shareMode, nint pFormat, out nint ppClosestMatch);

        [PreserveSig]
        int GetMixFormat(out nint ppDeviceFormat);

        [PreserveSig]
        int GetDevicePeriod(out long hnsDefaultDevicePeriod, out long hnsMinimumDevicePeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(nint eventHandle);

        [PreserveSig]
        int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object? ppv);
    }

    [ComImport, Guid(AudioCaptureClientInterfaceId)]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(
            out nint ppData,
            out uint pNumFramesToRead,
            out uint pdwFlags,
            out ulong pu64DevicePosition,
            out ulong pu64QpcPosition);

        [PreserveSig]
        int ReleaseBuffer(uint numFramesRead);

        [PreserveSig]
        int GetNextPacketSize(out uint pNumFramesInNextPacket);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DeviceNotificationClient(Action onDefaultRenderChanged) : IMMNotificationClient
    {
        public int OnDeviceStateChanged(string deviceId, uint newState) => 0;
        public int OnDeviceAdded(string deviceId) => 0;
        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnDefaultDeviceChanged(int dataFlow, int role, string? defaultDeviceId)
        {
            if (dataFlow == DataFlowRender && role == RoleMultimedia)
            {
                onDefaultRenderChanged();
            }

            return 0;
        }

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateEventW(
        nint lpEventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bManualReset,
        [MarshalAs(UnmanagedType.Bool)] bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(
        uint nCount,
        nint[] lpHandles,
        [MarshalAs(UnmanagedType.Bool)] bool bWaitAll,
        uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);
}
