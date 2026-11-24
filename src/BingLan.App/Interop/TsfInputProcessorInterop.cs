using System.Diagnostics;
using System.Runtime.InteropServices;
using BingLan.Core.TopBar;

namespace BingLan.App.Interop;

/// <summary>
/// The TSF surface behind the taskbar's language indicator. The profile manager tells
/// which text input processor (an IME such as Microsoft Pinyin or WeChat IME) is active,
/// enumerates enabled input methods and activates a chosen profile across the session.
/// GUIDs and vtable order come from the Windows SDK's msctf.idl; the coclasses live in msctf.dll.
/// </summary>
internal static class TsfInputProcessorInterop
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct InputProcessorProfile
    {
        internal uint ProfileType;      // 1 = text input processor, 2 = plain keyboard layout
        internal ushort LanguageId;
        internal Guid Clsid;
        internal Guid ProfileGuid;
        internal Guid CategoryGuid;
        internal nint SubstituteLayout;
        internal uint Caps;
        internal nint Layout;
        internal uint Flags;
    }

    internal readonly record struct ActiveProfileSnapshot(
        string? ProfileKey,
        string? Name,
        bool IsInputProcessor,
        ushort LanguageId,
        nint Layout);

    internal const uint ProfileTypeInputProcessor = 1;
    internal const uint ProfileTypeKeyboardLayout = 2;

    private const uint TfIppFlagActive = 0x00000001u;
    private const uint TfIppFlagEnabled = 0x00000002u;
    private const uint TfIppFlagSubstitutedByInputProcessor = 0x00000004u;
    private const uint TfIppmfForSession = 0x20000000u;
    private const uint TfIppmfDontCareCurrentInputLanguage = 0x00000004u;
    private const uint TfIpSinkFlagActive = 0x00000001u;

    internal static readonly Guid KeyboardCategory =
        new("34745C63-B2F0-4784-8B67-5E12C8701A31");

    private static readonly Guid ThreadMgrClsid =
        new("529A9E6B-6587-4F23-AB9E-9C7D683E3C50");

    private static readonly Guid InputProcessorProfilesClsid =
        new("33C53A50-F456-4884-B049-85FD643ECFED");

    private static readonly Guid ProfileActivationSinkIid =
        new("71c6e74e-0f28-11d8-a82a-00065b84435c");

    private static object CreateComObject(Guid clsid)
    {
        // Activator keeps the RCW a plain __ComObject; the interface cast below asks the
        // QueryInterface that matters, sidestepping ComImport coclass quirks.
        var type = Type.GetTypeFromCLSID(clsid)
            ?? throw new InvalidOperationException($"TSF 类 {clsid} 不可用");
        return Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"TSF 类 {clsid} 创建失败");
    }

    [ComImport, Guid("aa80e801-2021-11d2-93e0-0060b067b86e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfThreadMgr
    {
        void Activate(out uint clientId);
        void Deactivate();
    }

    [ComImport, Guid("4ea48a35-60ae-446f-8fd6-e6a8d82459f7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfSource
    {
        [PreserveSig] int AdviseSink(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] object punk, out uint cookie);
        [PreserveSig] int UnadviseSink(uint cookie);
    }

    [ComImport, Guid("71c6e74e-0f28-11d8-a82a-00065b84435c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileActivationSink
    {
        [PreserveSig] int OnActivated(
            uint profileType,
            ushort langid,
            ref Guid clsid,
            ref Guid catid,
            ref Guid profileGuid,
            nint layout,
            uint flags);
    }

    [ComImport, Guid("71c6e74d-0f28-11d8-a82a-00065b84435c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumTfInputProcessorProfiles
    {
        [PreserveSig] int Clone([MarshalAs(UnmanagedType.Interface)] out IEnumTfInputProcessorProfiles? enumerator);
        [PreserveSig] int Next(uint count, out InputProcessorProfile profile, out uint fetched);
        [PreserveSig] int Reset();
        [PreserveSig] int Skip(uint count);
    }

    [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileMgr
    {
        // Slots 3-9 are declared in msctf.idl order up to GetActiveProfile.
        [PreserveSig] int ActivateProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, nint layout, uint flags);
        [PreserveSig] int DeactivateProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, nint layout, uint flags);
        [PreserveSig] int GetProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, nint layout, out InputProcessorProfile result);
        [PreserveSig] int EnumProfiles(ushort langid, [MarshalAs(UnmanagedType.Interface)] out IEnumTfInputProcessorProfiles? enumerator);
        [PreserveSig] int ReleaseInputProcessor(ref Guid clsid, uint flags);
        [PreserveSig] int RegisterProfile(ref Guid clsid, ushort langid, ref Guid profile, string description, uint descriptionLength, string iconFile, uint iconFileLength, uint iconIndex, nint substituteLayout, uint preferredLayout, int enabledByDefault, uint flags);
        [PreserveSig] int UnregisterProfile(ref Guid clsid, ushort langid, ref Guid profile, uint flags);
        [PreserveSig] int GetActiveProfile(ref Guid category, out InputProcessorProfile profile);
    }

    [ComImport, Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfiles
    {
        // Slots 3-11 only keep the vtable aligned up to GetLanguageProfileDescription.
        [PreserveSig] int Register(ref Guid clsid);
        [PreserveSig] int Unregister(ref Guid clsid);
        [PreserveSig] int AddLanguageProfile(ref Guid clsid, ushort langid, ref Guid profile, string description, uint descriptionLength, string iconFile, uint iconFileLength, uint iconIndex);
        [PreserveSig] int RemoveLanguageProfile(ref Guid clsid, ushort langid, ref Guid profile);
        [PreserveSig] int EnumInputProcessorInfo(out nint enumerator);
        [PreserveSig] int GetDefaultLanguageProfile(ushort langid, ref Guid category, out Guid clsid, out Guid profile);
        [PreserveSig] int SetDefaultLanguageProfile(ushort langid, ref Guid clsid, ref Guid profile);
        [PreserveSig] int ActivateLanguageProfile(ref Guid clsid, ushort langid, ref Guid profile);
        [PreserveSig] int GetActiveLanguageProfile(ref Guid clsid, out ushort langid, out Guid profile);
        [PreserveSig] int GetLanguageProfileDescription(ref Guid clsid, ushort langid, ref Guid profile, out nint description);
    }

    /// <summary>
    /// Reads the currently active keyboard input processor or layout profile on the calling STA
    /// thread. Returns a default snapshot when TSF is unavailable.
    /// </summary>
    internal static ActiveProfileSnapshot ReadActiveInputProcessor()
    {
        try
        {
            var threadMgr = (ITfThreadMgr)CreateComObject(ThreadMgrClsid);
            threadMgr.Activate(out _);
            try
            {
                var profiles = CreateComObject(InputProcessorProfilesClsid);
                var manager = (ITfInputProcessorProfileMgr)profiles;
                var category = KeyboardCategory;
                if (manager.GetActiveProfile(ref category, out var profile) != 0)
                {
                    return default;
                }

                if (profile.ProfileType == ProfileTypeInputProcessor)
                {
                    var name = ReadProfileDescription((ITfInputProcessorProfiles)profiles, ref profile);
                    var key = TopBarModuleRules.BuildInputMethodKey(
                        isInputProcessor: true,
                        profile.LanguageId,
                        profile.Clsid,
                        profile.ProfileGuid,
                        profile.Layout);
                    return new ActiveProfileSnapshot(key, name, IsInputProcessor: true, profile.LanguageId, profile.Layout);
                }

                var layout = profile.Layout != 0 ? profile.Layout : profile.SubstituteLayout;
                var layoutKey = TopBarModuleRules.BuildInputMethodKey(
                    isInputProcessor: false,
                    profile.LanguageId,
                    Guid.Empty,
                    Guid.Empty,
                    layout);
                return new ActiveProfileSnapshot(layoutKey, null, IsInputProcessor: false, profile.LanguageId, layout);
            }
            finally
            {
                threadMgr.Deactivate();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Debug.WriteLine($"读取活动输入法失败：{ex.Message}");
            return default;
        }
    }

    /// <summary>
    /// The display name of the active text input processor, or null when the active
    /// profile is a plain keyboard layout or the call fails. TSF wants an STA thread:
    /// call this from the UI thread.
    /// </summary>
    internal static string? ReadActiveInputProcessorName() =>
        ReadActiveInputProcessor().Name;

    /// <summary>
    /// Enumerates all enabled keyboard input methods and layouts for the current user session,
    /// filtering out dummy layouts substituted by a TSF input processor.
    /// </summary>
    internal static IReadOnlyList<TopBarInputMethodOption> EnumerateEnabledProfiles(
        Func<nint, string> layoutNameResolver)
    {
        try
        {
            var threadMgr = (ITfThreadMgr)CreateComObject(ThreadMgrClsid);
            threadMgr.Activate(out _);
            try
            {
                var profiles = CreateComObject(InputProcessorProfilesClsid);
                var manager = (ITfInputProcessorProfileMgr)profiles;
                var descriptions = (ITfInputProcessorProfiles)profiles;

                var activeKey = string.Empty;
                var category = KeyboardCategory;
                if (manager.GetActiveProfile(ref category, out var active) == 0)
                {
                    var activeIsTip = active.ProfileType == ProfileTypeInputProcessor;
                    var activeLayout = active.Layout != 0 ? active.Layout : active.SubstituteLayout;
                    activeKey = TopBarModuleRules.BuildInputMethodKey(
                        activeIsTip,
                        active.LanguageId,
                        activeIsTip ? active.Clsid : Guid.Empty,
                        activeIsTip ? active.ProfileGuid : Guid.Empty,
                        activeIsTip ? 0 : activeLayout);
                }

                if (manager.EnumProfiles(0, out var enumerator) != 0 || enumerator is null)
                {
                    return [];
                }

                var raw = new List<TopBarInputMethodOption>();
                try
                {
                    while (enumerator.Next(1, out var profile, out var fetched) == 0 && fetched == 1)
                    {
                        if ((profile.Flags & TfIppFlagEnabled) == 0
                            || (profile.Flags & TfIppFlagSubstitutedByInputProcessor) != 0)
                        {
                            continue;
                        }

                        if (profile.ProfileType == ProfileTypeInputProcessor)
                        {
                            if (profile.CategoryGuid != KeyboardCategory)
                            {
                                continue;
                            }
                            var name = ReadProfileDescription(descriptions, ref profile);
                            if (string.IsNullOrWhiteSpace(name))
                            {
                                continue;
                            }
                            var key = TopBarModuleRules.BuildInputMethodKey(
                                isInputProcessor: true,
                                profile.LanguageId,
                                profile.Clsid,
                                profile.ProfileGuid,
                                0);
                            var isActive = (profile.Flags & TfIppFlagActive) != 0
                                || string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase);
                            raw.Add(new TopBarInputMethodOption(
                                key,
                                name,
                                TopBarModuleRules.FormatInputMethodLanguageTag(profile.LanguageId),
                                profile.LanguageId,
                                IsInputProcessor: true,
                                isActive,
                                profile.Layout != 0 ? profile.Layout : profile.SubstituteLayout,
                                profile.Clsid,
                                profile.ProfileGuid));
                        }
                        else if (profile.ProfileType == ProfileTypeKeyboardLayout)
                        {
                            var layout = profile.Layout != 0 ? profile.Layout : profile.SubstituteLayout;
                            if (layout == 0)
                            {
                                continue;
                            }
                            var name = layoutNameResolver(layout);
                            var key = TopBarModuleRules.BuildInputMethodKey(
                                isInputProcessor: false,
                                profile.LanguageId,
                                Guid.Empty,
                                Guid.Empty,
                                layout);
                            var isActive = (profile.Flags & TfIppFlagActive) != 0
                                || string.Equals(key, activeKey, StringComparison.OrdinalIgnoreCase);
                            raw.Add(new TopBarInputMethodOption(
                                key,
                                name,
                                TopBarModuleRules.FormatInputMethodLanguageTag(profile.LanguageId),
                                profile.LanguageId,
                                IsInputProcessor: false,
                                isActive,
                                layout,
                                Guid.Empty,
                                Guid.Empty));
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(enumerator);
                }

                return TopBarModuleRules.NormalizeInputMethodOptions(raw);
            }
            finally
            {
                threadMgr.Deactivate();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Debug.WriteLine($"枚举输入法失败：{ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Activates the chosen TSF input processor or keyboard layout for the current session.
    /// For legacy/plain keyboard layouts, also posts WM_INPUTLANGCHANGEREQUEST to the target
    /// foreground window when per-window input method mode keeps its previous HKL.
    /// </summary>
    internal static bool ActivateInputMethod(TopBarInputMethodOption option, nint foregroundWindow)
    {
        try
        {
            var threadMgr = (ITfThreadMgr)CreateComObject(ThreadMgrClsid);
            threadMgr.Activate(out _);
            try
            {
                var profiles = CreateComObject(InputProcessorProfilesClsid);
                var manager = (ITfInputProcessorProfileMgr)profiles;
                var profileType = option.IsInputProcessor ? ProfileTypeInputProcessor : ProfileTypeKeyboardLayout;
                var clsid = option.IsInputProcessor ? option.Clsid : Guid.Empty;
                var profileGuid = option.IsInputProcessor ? option.ProfileGuid : Guid.Empty;
                var layout = option.IsInputProcessor ? 0 : option.KeyboardLayout;

                var hr = manager.ActivateProfile(
                    profileType,
                    option.LanguageId,
                    ref clsid,
                    ref profileGuid,
                    layout,
                    TfIppmfForSession);
                if (hr != 0)
                {
                    hr = manager.ActivateProfile(
                        profileType,
                        option.LanguageId,
                        ref clsid,
                        ref profileGuid,
                        layout,
                        TfIppmfForSession | TfIppmfDontCareCurrentInputLanguage);
                }

                if (!option.IsInputProcessor && option.KeyboardLayout != 0 && foregroundWindow != 0)
                {
                    var threadId = DockNativeMethods.GetWindowThreadProcessId(foregroundWindow, out _);
                    var currentLayout = threadId != 0 ? TopBarNativeMethods.GetKeyboardLayout(threadId) : 0;
                    if (currentLayout != option.KeyboardLayout)
                    {
                        var posted = DockNativeMethods.PostMessageW(
                            foregroundWindow,
                            TopBarNativeMethods.WmInputLangChangeRequest,
                            0,
                            option.KeyboardLayout);
                        if (hr != 0 && posted)
                        {
                            return true;
                        }
                    }
                }

                return hr == 0;
            }
            finally
            {
                threadMgr.Deactivate();
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Debug.WriteLine($"切换输入法失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Subscribes to TSF profile activation notifications on the calling STA thread so same-HKL
    /// switches (e.g. Microsoft Pinyin ↔ WeChat/Sogou IME) refresh immediately.
    /// </summary>
    internal static IDisposable? SubscribeProfileActivation(Action onActivated)
    {
        try
        {
            var threadMgr = (ITfThreadMgr)CreateComObject(ThreadMgrClsid);
            threadMgr.Activate(out _);
            try
            {
                var source = (ITfSource)threadMgr;
                var sink = new ProfileActivationSink(onActivated);
                var iid = ProfileActivationSinkIid;
                if (source.AdviseSink(ref iid, sink, out var cookie) != 0)
                {
                    threadMgr.Deactivate();
                    return null;
                }
                return new ProfileActivationSubscription(threadMgr, source, sink, cookie);
            }
            catch
            {
                threadMgr.Deactivate();
                throw;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
        {
            Debug.WriteLine($"订阅输入法切换通知失败：{ex.Message}");
            return null;
        }
    }

    private static string? ReadProfileDescription(
        ITfInputProcessorProfiles descriptions,
        ref InputProcessorProfile profile)
    {
        if (descriptions.GetLanguageProfileDescription(
                ref profile.Clsid, profile.LanguageId, ref profile.ProfileGuid, out var namePtr) != 0
            || namePtr == 0)
        {
            return null;
        }

        try
        {
            var name = Marshal.PtrToStringBSTR(namePtr);
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        finally
        {
            Marshal.FreeBSTR(namePtr);
        }
    }

    private sealed class ProfileActivationSink(Action onActivated) : ITfInputProcessorProfileActivationSink
    {
        public int OnActivated(
            uint profileType,
            ushort langid,
            ref Guid clsid,
            ref Guid catid,
            ref Guid profileGuid,
            nint layout,
            uint flags)
        {
            if ((flags & TfIpSinkFlagActive) != 0)
            {
                onActivated();
            }
            return 0;
        }
    }

    private sealed class ProfileActivationSubscription(
        ITfThreadMgr threadMgr,
        ITfSource source,
        ProfileActivationSink sink,
        uint cookie) : IDisposable
    {
        // Keep a strong reference to the sink for the lifetime of the subscription.
        private readonly ProfileActivationSink _sink = sink;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            try
            {
                _ = source.UnadviseSink(cookie);
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
            {
                Debug.WriteLine($"取消输入法切换通知失败：{ex.Message}");
            }
            try
            {
                threadMgr.Deactivate();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException)
            {
                Debug.WriteLine($"释放 TSF 线程激活失败：{ex.Message}");
            }
            GC.KeepAlive(_sink);
        }
    }
}
