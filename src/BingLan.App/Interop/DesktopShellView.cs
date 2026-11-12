using System.Runtime.InteropServices;
using BingLan.Core.Desktop;

namespace BingLan.App.Interop;

/// <summary>
/// The Windows desktop's folder view, reached through the documented Shell automation
/// path (ShellWindows → top-level browser → active view). Each instance is looked up
/// fresh so a restarted Explorer is never addressed through stale objects.
/// </summary>
internal sealed class DesktopShellView : IDisposable
{
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private static readonly Guid ShellWindowsClassId = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowserServiceId = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid ShellBrowserInterfaceId = new("000214E2-0000-0000-C000-000000000046");

    private readonly List<object> _comObjects = [];
    private IFolderView2 _folderView = null!;

    private DesktopShellView()
    {
    }

    /// <summary>Finds the current desktop view; throws when Explorer does not offer one.</summary>
    internal static DesktopShellView Open()
    {
        var view = new DesktopShellView();
        try
        {
            view.Bind();
            return view;
        }
        catch
        {
            view.Dispose();
            throw;
        }
    }

    private void Bind()
    {
        var shellWindowsType = Type.GetTypeFromCLSID(ShellWindowsClassId, throwOnError: true)!;
        var shellWindows = Track((IShellWindows)Activator.CreateInstance(shellWindowsType)!);
        object location = CsidlDesktop;
        object? root = null;
        Check(shellWindows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch, out var dispatch));
        var serviceProvider = (IComServiceProvider)Track(dispatch ?? throw new COMException("找不到桌面视图"));
        var serviceId = TopLevelBrowserServiceId;
        var browserId = ShellBrowserInterfaceId;
        Check(serviceProvider.QueryService(ref serviceId, ref browserId, out var browser));
        var shellBrowser = (IShellBrowser)Track(browser ?? throw new COMException("找不到桌面视图"));
        Check(shellBrowser.QueryActiveShellView(out var view));
        _folderView = (IFolderView2)Track(view ?? throw new COMException("找不到桌面视图"));
    }

    internal uint ReadFolderFlags()
    {
        Check(_folderView.GetCurrentFolderFlags(out var flags));
        return flags;
    }

    /// <summary>Writes only the icon visibility bit and reads the result back, retrying briefly if Explorer is still settling.</summary>
    internal bool SetIconsHidden(bool hidden)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var flags = CleanDesktopRules.Apply(ReadFolderFlags(), hidden);
            var hr = _folderView.SetCurrentFolderFlags(CleanDesktopRules.NoIconsFlag, flags);
            if (hr < 0)
            {
                if (attempt < 2)
                {
                    Thread.Sleep(80);
                    continue;
                }
                Check(hr);
            }
            if (CleanDesktopRules.AreIconsHidden(ReadFolderFlags()) == hidden)
            {
                return true;
            }
            if (attempt < 2)
            {
                Thread.Sleep(80);
            }
        }
        return CleanDesktopRules.AreIconsHidden(ReadFolderFlags()) == hidden;
    }

    public void Dispose()
    {
        for (var index = _comObjects.Count - 1; index >= 0; index--)
        {
            Marshal.ReleaseComObject(_comObjects[index]);
        }
        _comObjects.Clear();
    }

    private T Track<T>(T comObject) where T : class
    {
        if (!_comObjects.Contains(comObject))
        {
            _comObjects.Add(comObject);
        }
        return comObject;
    }

    private static void Check(int result)
    {
        if (result != 0)
        {
            Marshal.ThrowExceptionForHR(result == 1 ? unchecked((int)0x80004005) : result);
        }
    }

    // Only the members called here are typed; the others hold their vtable slots.
    [ComImport]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    [InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void GetCount();
        void Item();
        void NewEnum();
        void Register();
        void RegisterPending();
        void Revoke();
        void OnNavigate();
        void OnActivated();

        [PreserveSig]
        int FindWindowSW(
            [MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object? locationRoot,
            int windowClass,
            out int windowHandle,
            int options,
            [MarshalAs(UnmanagedType.IDispatch)] out object? dispatch);
    }

    [ComImport]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IComServiceProvider
    {
        [PreserveSig]
        int QueryService(
            ref Guid serviceId,
            ref Guid interfaceId,
            [MarshalAs(UnmanagedType.IUnknown)] out object? service);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow();
        void ContextSensitiveHelp();
        void InsertMenusSB();
        void SetMenuSB();
        void RemoveMenusSB();
        void SetStatusTextSB();
        void EnableModelessSB();
        void TranslateAcceleratorSB();
        void BrowseObject();
        void GetViewStateStream();
        void GetControlWindow();
        void SendControlMsg();

        [PreserveSig]
        int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object? view);
    }

    [ComImport]
    [Guid("1AF3A467-214F-4298-908E-06B03E0B39F9")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        void GetCurrentViewMode();
        void SetCurrentViewMode();
        void GetFolder();
        void Item();
        void ItemCount();
        void Items();
        void GetSelectionMarkedItem();
        void GetFocusedItem();
        void GetItemPosition();
        void GetSpacing();
        void GetDefaultSpacing();
        void GetAutoArrange();
        void SelectItem();
        void SelectAndPositionItems();
        void SetGroupBy();
        void GetGroupBy();
        void SetViewProperty();
        void GetViewProperty();
        void SetTileViewProperties();
        void SetExtendedTileViewProperties();
        void SetText();

        [PreserveSig]
        int SetCurrentFolderFlags(uint mask, uint flags);

        [PreserveSig]
        int GetCurrentFolderFlags(out uint flags);
    }
}
