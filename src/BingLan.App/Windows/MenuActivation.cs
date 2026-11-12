using System.Windows;
using ContextMenu = System.Windows.Controls.ContextMenu;
using System.Windows.Interop;
using BingLan.App.Interop;

namespace BingLan.App.Windows;

/// <summary>
/// Menus opened from windows that never activate (Dock, top bar) do not see clicks
/// elsewhere, so they would stay open until clicked again. Bringing the menu's own
/// popup to the foreground lets a click anywhere else dismiss it, as native menus do.
/// </summary>
internal static class MenuActivation
{
    public static ContextMenu DismissOnOutsideClick(this ContextMenu menu)
    {
        menu.Opened += (_, _) =>
        {
            if (PresentationSource.FromVisual(menu) is HwndSource source)
            {
                DockNativeMethods.SetForegroundWindow(source.Handle);
            }
        };
        return menu;
    }

    public static nint PopupHandle(this ContextMenu? menu) =>
        menu is { IsOpen: true } && PresentationSource.FromVisual(menu) is HwndSource source
            ? source.Handle
            : 0;
}
