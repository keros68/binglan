using System.Runtime.InteropServices;

namespace BingLan.App.Interop;

/// <summary>
/// The TSF surface behind the taskbar's language indicator. The profile manager tells
/// which text input processor (an IME such as Microsoft Pinyin or WeChat IME) is active,
/// and its description call names it. GUIDs and vtable order come from the Windows SDK's
/// msctf.idl; the coclasses both live in msctf.dll.
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

    internal const uint ProfileTypeInputProcessor = 1;

    internal static readonly Guid KeyboardCategory =
        new("34745C63-B2F0-4784-8B67-5E12C8701A31");

    private static readonly Guid ThreadMgrClsid =
        new("529A9E6B-6587-4F23-AB9E-9C7D683E3C50");

    private static readonly Guid InputProcessorProfilesClsid =
        new("33C53A50-F456-4884-B049-85FD643ECFED");

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
        void Activate(out Guid clientId);
        void Deactivate();
    }

    [ComImport, Guid("71c6e74c-0f28-11d8-a82a-00065b84435c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITfInputProcessorProfileMgr
    {
        // Slots 3-9 are only declared to keep the vtable aligned up to GetActiveProfile.
        [PreserveSig] int ActivateProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, nint layout, uint flags);
        [PreserveSig] int DeactivateProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, nint layout, uint flags);
        [PreserveSig] int GetProfile(uint type, ushort langid, ref Guid clsid, ref Guid profile, nint layout, out InputProcessorProfile result);
        [PreserveSig] int EnumProfiles(ushort langid, out nint enumerator);
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
    /// The display name of the active text input processor, or null when the active
    /// profile is a plain keyboard layout or the call fails. TSF wants an STA thread:
    /// call this from the UI thread.
    /// </summary>
    internal static string? ReadActiveInputProcessorName()
    {
        var threadMgr = (ITfThreadMgr)CreateComObject(ThreadMgrClsid);
        threadMgr.Activate(out _);
        try
        {
            var profiles = CreateComObject(InputProcessorProfilesClsid);
            var manager = (ITfInputProcessorProfileMgr)profiles;
            var category = KeyboardCategory;
            if (manager.GetActiveProfile(ref category, out var profile) != 0
                || profile.ProfileType != ProfileTypeInputProcessor)
            {
                return null;
            }

            var descriptions = (ITfInputProcessorProfiles)profiles;
            if (descriptions.GetLanguageProfileDescription(
                    ref profile.Clsid, profile.LanguageId, ref profile.ProfileGuid, out var namePtr) != 0
                || namePtr == 0)
            {
                return null;
            }

            var name = Marshal.PtrToStringBSTR(namePtr);
            Marshal.FreeBSTR(namePtr);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        finally
        {
            threadMgr.Deactivate();
        }
    }
}
