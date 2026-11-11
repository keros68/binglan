using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BingLan.App.Interop;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Dock;

internal sealed record DockAppVisual(ImageSource? Icon, string? DisplayName);

/// <summary>
/// Resolves Dock icons and friendly names off the UI thread. Packaged and web apps
/// resolve through the Start menu apps folder, desktop apps through their executable.
/// </summary>
internal static class DockAppResolver
{
    private const string AppsFolderPrefix = "shell:AppsFolder\\";

    /// <summary>Where custom icons of pinned apps are kept; set once by the app host.</summary>
    internal static DockIconStore? IconStore { get; set; }

    internal static DockAppVisual Resolve(DockItem item)
    {
        if (item.Pinned?.IconFile is { } iconFile && IconStore?.Load(iconFile) is { } customIcon)
        {
            return new DockAppVisual(customIcon, null);
        }

        if (item.Pinned is { } pathPin && DockPinRules.IsPathPin(pathPin))
        {
            return ResolvePath(pathPin.TargetPath!);
        }

        var appUserModelId = item.Pinned?.AppUserModelId ?? GetGroupAppUserModelId(item.Group);
        if (appUserModelId is not null)
        {
            var packaged = FromAppsFolder(appUserModelId);
            if (packaged.Icon is not null)
            {
                return packaged;
            }
        }

        var executablePath = item.Pinned?.ExecutablePath
            ?? item.Group?.Windows
                .Select(window => window.ExecutablePath)
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
        if (executablePath is not null && File.Exists(executablePath) && !IsFrameHost(executablePath))
        {
            var icon = IconFromParsingName(executablePath, isPath: true);
            if (icon is not null)
            {
                return new DockAppVisual(icon, GetFileDescription(executablePath));
            }
        }

        var window = item.Group?.Windows.FirstOrDefault();
        var windowIcon = window is not null ? FromWindow(window.Handle) : null;
        return new DockAppVisual(
            windowIcon ?? IconFromParsingName("application.exe", isPath: false),
            window is not null && IsFrameHost(executablePath) && !string.IsNullOrWhiteSpace(window.Title)
                ? window.Title
                : null);
    }

    // The shell's own icon: the folder icon, or the icon of the file's type or its
    // program. A target that is gone gets the generic folder or document icon.
    private static DockAppVisual ResolvePath(string path)
    {
        if (Directory.Exists(path) || File.Exists(path))
        {
            if (IconFromParsingName(path, isPath: true) is { } icon)
            {
                return new DockAppVisual(icon, null);
            }
        }

        var generic = Path.HasExtension(path) ? Path.GetExtension(path) : "folder";
        return new DockAppVisual(IconFromParsingName(generic, isPath: false), null);
    }

    internal static bool AppsFolderItemExists(string appUserModelId)
    {
        if (DockNativeMethods.SHParseDisplayName(
                AppsFolderPrefix + appUserModelId,
                0,
                out var itemIdList,
                0,
                out _) != 0
            || itemIdList == 0)
        {
            return false;
        }

        DockNativeMethods.ILFree(itemIdList);
        return true;
    }

    internal static string AppsFolderPath(string appUserModelId) => AppsFolderPrefix + appUserModelId;

    internal static bool IsFrameHost(string? executablePath) =>
        executablePath is not null
        && string.Equals(
            Path.GetFileName(executablePath),
            "ApplicationFrameHost.exe",
            StringComparison.OrdinalIgnoreCase);

    // Store app binaries live in a protected, versioned folder that users cannot open
    // and that changes on every update; such apps are identified by app model ID.
    internal static bool IsPackagedPath(string? executablePath) =>
        executablePath is not null
        && executablePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase);

    internal static string? GetFileDescription(string executablePath)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(executablePath).FileDescription;
            return string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? GetGroupAppUserModelId(WindowGroup? group) =>
        group is not null && group.Key.StartsWith("aumid:", StringComparison.OrdinalIgnoreCase)
            ? group.Key["aumid:".Length..]
            : null;

    private static DockAppVisual FromAppsFolder(string appUserModelId)
    {
        if (DockNativeMethods.SHParseDisplayName(
                AppsFolderPrefix + appUserModelId,
                0,
                out var itemIdList,
                0,
                out _) != 0
            || itemIdList == 0)
        {
            return new DockAppVisual(null, null);
        }

        try
        {
            var info = NewFileInfo();
            var result = DockNativeMethods.SHGetFileInfoW(
                itemIdList,
                0,
                ref info,
                (uint)Marshal.SizeOf<DockNativeMethods.ShFileInfo>(),
                DockNativeMethods.ShgfiPidl
                    | DockNativeMethods.ShgfiSysIconIndex
                    | DockNativeMethods.ShgfiDisplayName);
            return result == 0
                ? new DockAppVisual(null, null)
                : new DockAppVisual(
                    FromSystemImageList(info.IconIndex),
                    string.IsNullOrWhiteSpace(info.DisplayName) ? null : info.DisplayName);
        }
        finally
        {
            DockNativeMethods.ILFree(itemIdList);
        }
    }

    private static ImageSource? IconFromParsingName(string path, bool isPath)
    {
        var info = NewFileInfo();
        var result = DockNativeMethods.SHGetFileInfoW(
            path,
            isPath ? 0 : DockNativeMethods.FileAttributeNormal,
            ref info,
            (uint)Marshal.SizeOf<DockNativeMethods.ShFileInfo>(),
            DockNativeMethods.ShgfiSysIconIndex
                | (isPath ? 0 : DockNativeMethods.ShgfiUseFileAttributes));
        return result == 0 ? null : FromSystemImageList(info.IconIndex);
    }

    private static ImageSource? FromSystemImageList(int iconIndex)
    {
        if (DockNativeMethods.SHGetImageList(
                DockNativeMethods.ShilExtraLarge,
                DockNativeMethods.ImageListId,
                out var imageList) != 0
            || imageList == 0)
        {
            return null;
        }

        try
        {
            var icon = DockNativeMethods.ImageList_GetIcon(
                imageList,
                iconIndex,
                DockNativeMethods.IldTransparent);
            return icon == 0 ? null : ToImageSource(icon, ownsHandle: true);
        }
        finally
        {
            Marshal.Release(imageList);
        }
    }

    private static ImageSource? FromWindow(nint window)
    {
        var icon = QueryWindowIcon(window, DockNativeMethods.IconBig);
        if (icon == 0)
        {
            icon = QueryWindowIcon(window, DockNativeMethods.IconSmall2);
        }
        if (icon == 0)
        {
            icon = QueryWindowIcon(window, DockNativeMethods.IconSmall);
        }
        if (icon == 0)
        {
            icon = DockNativeMethods.GetClassLongPtr(window, DockNativeMethods.GclpHIcon);
        }
        if (icon == 0)
        {
            icon = DockNativeMethods.GetClassLongPtr(window, DockNativeMethods.GclpHIconSm);
        }

        return icon == 0 ? null : ToImageSource(icon, ownsHandle: false);
    }

    private static nint QueryWindowIcon(nint window, int iconKind)
    {
        DockNativeMethods.SendMessageTimeoutW(
            window,
            DockNativeMethods.WmGetIcon,
            iconKind,
            0,
            DockNativeMethods.SmtoAbortIfHung,
            250,
            out var icon);
        return icon;
    }

    private static ImageSource? ToImageSource(nint icon, bool ownsHandle)
    {
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception exception) when (exception is COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (ownsHandle)
            {
                DockNativeMethods.DestroyIcon(icon);
            }
        }
    }

    private static DockNativeMethods.ShFileInfo NewFileInfo() =>
        new() { DisplayName = string.Empty, TypeName = string.Empty };

    internal static DockPinnedApp? CreatePin(DockItem item, string displayName)
    {
        if (item.Group is null)
        {
            return null;
        }

        var pin = DockPinRules.CreateFromGroup(item.Group, displayName);
        if (pin is null)
        {
            return null;
        }

        if (IsFrameHost(pin.ExecutablePath))
        {
            pin.ExecutablePath = null;
        }
        // Only keep an app model ID that the shell can launch again after the app exits.
        if (pin.AppUserModelId is not null && !AppsFolderItemExists(pin.AppUserModelId))
        {
            pin.AppUserModelId = null;
        }
        if (pin.AppUserModelId is not null && IsPackagedPath(pin.ExecutablePath))
        {
            pin.ExecutablePath = null;
        }
        return pin.AppUserModelId is null && pin.ExecutablePath is null ? null : pin;
    }
}
