using System.IO;
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
    internal static IReadOnlyList<DockPinnedApp> ReadStartMenuApps()
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System
        };
        var apps = new Dictionary<string, DockPinnedApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[]
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
                if (CreatePin(shortcut) is { ExecutablePath: { } path } app && !apps.ContainsKey(path))
                {
                    apps[path] = app;
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
            apps[explorerPath] = fileExplorer;
        }
        return apps.Values.OrderBy(app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

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

    private sealed record ResolvedShortcut(string TargetPath, string Arguments, string WorkingDirectory);

    private static ResolvedShortcut? ResolveShortcut(string shortcut)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
        {
            return null;
        }

        try
        {
            dynamic shell = Activator.CreateInstance(shellType)!;
            var link = shell.CreateShortcut(shortcut);
            var targetPath = (string)link.TargetPath;
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                return null;
            }

            return new ResolvedShortcut(
                Environment.ExpandEnvironmentVariables(targetPath),
                // Arguments stay verbatim: they belong to the target program and may
                // contain text that looks like a variable (cmd's %CD%, format strings).
                ((string)link.Arguments).Trim(),
                Environment.ExpandEnvironmentVariables((string)link.WorkingDirectory).Trim());
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.COMException
            or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    }
}
