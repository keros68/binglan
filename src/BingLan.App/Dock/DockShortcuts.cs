using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using BingLan.Core.Dock;
using BingLan.Core.Models;

namespace BingLan.App.Dock;

/// <summary>
/// Turns programs and shortcuts into dock pins, and reads the apps the user pinned to the
/// Windows taskbar. Only shortcuts to programs become pins; shortcuts to documents,
/// folders or Store apps without a program path are skipped.
/// </summary>
internal static class DockShortcuts
{
    private const int StgmRead = 0;
    private const uint SlgpRawPath = 0x4;
    private const int PathBufferChars = 1024;
    private const int ArgumentsBufferChars = 2048;

    private static string TaskbarPinFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");

    /// <summary>The apps pinned to the Windows taskbar, in their file name order.</summary>
    internal static IReadOnlyList<DockPinnedApp> ReadTaskbarPins()
    {
        if (!Directory.Exists(TaskbarPinFolder))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(TaskbarPinFolder, "*.lnk")
                .Order(StringComparer.CurrentCultureIgnoreCase)
                .Select(CreatePin)
                .OfType<DockPinnedApp>()
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    // Start menu entries that are not apps people pin.
    private static readonly string[] SkippedNames = ["卸载", "uninstall", "帮助", "help", "readme", "说明", "官网", "website", "修复", "repair"];

    /// <summary>
    /// Programs in the current user's and the shared Start menu, one per program, sorted
    /// by name. Uninstallers, help files and other non-app entries are left out.
    /// </summary>
    internal static IReadOnlyList<DockPinnedApp> ReadStartMenuApps() =>
        ReadStartMenuApps(preserveLaunchVariants: false);

    internal static IReadOnlyList<DockPinnedApp> ReadStartMenuApps(bool preserveLaunchVariants,
        IDictionary<DockPinnedApp, string>? startMenuFolders = null,
        IEnumerable<string>? startMenuRoots = null)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };
        var apps = new Dictionary<string, DockPinnedApp>(preserveLaunchVariants
            ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
        foreach (var folder in startMenuRoots ?? new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu)
                 })
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }
            foreach (var shortcut in Directory.EnumerateFiles(folder, "*.lnk", options))
            {
                var name = Path.GetFileNameWithoutExtension(shortcut);
                if (SkippedNames.Any(skipped => name.Contains(skipped, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
                if (CreatePin(shortcut) is { ExecutablePath: { } path } app)
                {
                    if (apps.TryAdd(Key(path, app), app) && startMenuFolders is not null
                        && Path.GetFileName(Path.GetDirectoryName(shortcut)) is { Length: > 0 } group)
                    {
                        startMenuFolders.Add(app, group);
                    }
                }
            }
        }
        // The Start menu's File Explorer entry points at a shell object rather than a
        // program, so it is added by its program here. Other shortcuts that start
        // explorer.exe (with a folder argument) would pin the same program, so this entry
        // replaces them.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        if (CreatePin(explorer) is { ExecutablePath: { } explorerPath } fileExplorer)
        {
            fileExplorer.DisplayName = "文件资源管理器";
            apps[Key(explorerPath, fileExplorer)] = fileExplorer;
        }
        return apps.Values.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();

        string Key(string path, DockPinnedApp app) => preserveLaunchVariants
            ? LaunchVariantKey(path, app.Arguments, app.WorkingDirectory) : path;
    }

    internal static string LaunchVariantKey(string path, string? arguments, string? workingDirectory)
    {
        var target = NormalizePath(path);
        var directory = workingDirectory is { Length: > 0 }
            ? NormalizePath(workingDirectory).TrimEnd('\\', '/') : null;
        if (Path.IsPathFullyQualified(target)
            && string.Equals(directory, Path.GetDirectoryName(target)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            directory = null;
        // Windows paths ignore case; arguments belong to the program and retain it.
        return string.Join('\0', target.TrimEnd('\\', '/').ToUpperInvariant(), arguments, directory?.ToUpperInvariant());

        static string NormalizePath(string path)
        {
            if (!Path.IsPathFullyQualified(path)) return path;
            try { return Path.GetFullPath(path); }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException) { return path; }
        }
    }

    /// <summary>
    /// Classifies a dropped file-system path using its real target and directory state.
    /// File-system and shortcut reads run through the background entry points below.
    /// </summary>
    private static DroppedPathInfo InspectDroppedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return new DroppedPathInfo(string.Empty, string.Empty, IsDirectory: false, IsProgram: false);
        }

        var trimmed = path.Trim();
        if (DockFileDropRules.IsExecutablePath(trimmed))
        {
            return new DroppedPathInfo(path, trimmed, IsDirectory: false, IsProgram: true);
        }

        if (string.Equals(Path.GetExtension(trimmed), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = ResolveShortcut(trimmed);
            if (resolved is null || DockFileDropRules.IsExecutablePath(resolved.TargetPath))
            {
                return new DroppedPathInfo(
                    path,
                    resolved?.TargetPath ?? trimmed,
                    IsDirectory: false,
                    IsProgram: true);
            }

            var targetIsDirectory = Directory.Exists(resolved.TargetPath);
            return new DroppedPathInfo(path, resolved.TargetPath, targetIsDirectory, IsProgram: false);
        }

        return new DroppedPathInfo(path, trimmed, Directory.Exists(trimmed), IsProgram: false);
    }

    internal static Task<IReadOnlyList<DroppedPathInfo>> InspectDroppedPathsAsync(IEnumerable<string> paths) =>
        Task.Run<IReadOnlyList<DroppedPathInfo>>(() => paths
            .Select(InspectDroppedPath)
            .ToArray());

    /// <summary>Reads dropped files, folders and shortcuts off the caller's thread.</summary>
    internal static Task<IReadOnlyList<DockPinnedApp>> CreatePinsAsync(IEnumerable<string> paths) =>
        Task.Run<IReadOnlyList<DockPinnedApp>>(() => paths
            .Select(path => CreatePin(path) ?? CreatePathPin(path))
            .OfType<DockPinnedApp>()
            .ToArray());

    /// <summary>A pin for a program or a shortcut to one, or null for anything else.</summary>
    internal static DockPinnedApp? CreatePin(string path)
    {
        var extension = Path.GetExtension(path);
        var resolved = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? ResolveShortcut(path)
            : null;
        var target = resolved?.TargetPath ?? path;
        if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(target))
        {
            return null;
        }

        var app = DockPinRules.CreateFromExecutable(target);
        if (app is null)
        {
            return null;
        }

        // A shortcut names how the program is meant to be started; without its
        // arguments and working directory some programs (AutoCAD, game launchers)
        // refuse to start. Like Explorer, an empty working directory means the
        // program's own folder.
        app.Arguments = resolved?.Arguments;
        app.WorkingDirectory = string.IsNullOrWhiteSpace(resolved?.WorkingDirectory)
            ? Path.GetDirectoryName(target)
            : resolved.WorkingDirectory;

        // A shortcut's own name ("微信") reads better than the program's ("Weixin").
        app.DisplayName = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(path)
            : DockAppResolver.GetFileDescription(target) ?? app.DisplayName;
        return app;
    }

    /// <summary>
    /// A pin for an existing folder or file, or for the folder or file a shortcut points
    /// at; null when nothing exists there. Only the path is recorded.
    /// </summary>
    internal static DockPinnedApp? CreatePathPin(string path)
    {
        var isShortcut = Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
        var target = isShortcut ? ResolveShortcut(path)?.TargetPath : path;
        if (string.IsNullOrWhiteSpace(target) || !(Directory.Exists(target) || File.Exists(target)))
        {
            return null;
        }

        // A file keeps its full name ("报告.docx") since the extension tells what it is.
        return DockPinRules.CreateFromPath(
            target,
            isShortcut ? Path.GetFileNameWithoutExtension(path) : null);
    }

    private sealed record ResolvedShortcut(string TargetPath, string Arguments, string WorkingDirectory);

    private static ResolvedShortcut? ResolveShortcut(string shortcut)
    {
        object? comObject = null;
        try
        {
            comObject = new ShellLink();
            var persistFile = (IPersistFile)comObject;
            // STGM_READ only; never call IShellLinkW.Resolve so offline network targets
            // cannot stall or show shell dialogs.
            persistFile.Load(shortcut, StgmRead);

            var link = (IShellLinkW)comObject;
            var pathBuilder = new StringBuilder(PathBufferChars);
            link.GetPath(pathBuilder, pathBuilder.Capacity, 0, 0);
            if (pathBuilder.Length == 0)
            {
                link.GetPath(pathBuilder, pathBuilder.Capacity, 0, SlgpRawPath);
            }

            var targetPath = pathBuilder.ToString();
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            var argsBuilder = new StringBuilder(ArgumentsBufferChars);
            link.GetArguments(argsBuilder, argsBuilder.Capacity);

            var workDirBuilder = new StringBuilder(PathBufferChars);
            link.GetWorkingDirectory(workDirBuilder, workDirBuilder.Capacity);

            return new ResolvedShortcut(
                Environment.ExpandEnvironmentVariables(targetPath),
                // Arguments stay verbatim: they belong to the target program and may
                // contain text that looks like a variable (cmd's %CD%, format strings).
                argsBuilder.ToString().Trim(),
                Environment.ExpandEnvironmentVariables(workDirBuilder.ToString()).Trim());
        }
        catch (Exception exception) when (exception is COMException or IOException or UnauthorizedAccessException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (comObject is not null && Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink;

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);
        void GetIDList(out nint ppidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(nint hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
