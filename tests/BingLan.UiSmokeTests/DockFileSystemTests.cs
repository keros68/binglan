using System.IO;
using System.Runtime.InteropServices;
using BingLan.App.Dock;
using BingLan.App.Services;
using BingLan.App.Windows;
using BingLan.Core.Dock;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

internal static class DockFileSystemTests
{
    internal static void PrepareDroppedPaths()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BingLanDockFiles", Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(directory, "folder.with.extension")).FullName;
            var file = Path.Combine(directory, "README");
            File.WriteAllText(file, "Dock path mapping regression");
            var missing = Path.Combine(directory, "missing.txt");
            var shortcutPath = Path.Combine(directory, "program.lnk");
            var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            CreateShortcut(shortcutPath, executable, "/c exit 0", directory);

            string[] paths = [folder, file, shortcutPath, missing];
            var infos = ReadOffCallerThread<DroppedPathInfo>(DockShortcuts.InspectDroppedPathsAsync, paths);
            Assert(infos[0].IsDirectory, "带扩展名的文件夹应按真实属性识别");
            Assert(!infos[1].IsDirectory && !infos[1].IsProgram, "无扩展名文件应按真实属性识别");
            Assert(infos[2].IsProgram && string.Equals(infos[2].ResolvedPath, executable, StringComparison.OrdinalIgnoreCase),
                $"后台应解析快捷方式的程序目标，实际：{infos[2].ResolvedPath}");

            var pins = ReadOffCallerThread<DockPinnedApp>(DockShortcuts.CreatePinsAsync, paths);
            Assert(pins.Count == 3, "只应固定存在的文件、文件夹和程序快捷方式");
            Assert(string.Equals(pins[0].TargetPath, folder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(pins[1].TargetPath, file, StringComparison.OrdinalIgnoreCase),
                "后台解析应保留拖入顺序和真实路径");
            Assert(string.Equals(pins[2].ExecutablePath, executable, StringComparison.OrdinalIgnoreCase)
                && pins[2].Arguments == "/c exit 0"
                && string.Equals(pins[2].WorkingDirectory, directory, StringComparison.OrdinalIgnoreCase),
                "后台解析应保留快捷方式参数与工作目录");
            Assert(Directory.Exists(folder) && File.ReadAllText(file) == "Dock path mapping regression"
                && File.Exists(shortcutPath), "准备固定项不能修改原文件");

            var startMenu = Directory.CreateDirectory(Path.Combine(directory, "StartMenu")).FullName;
            var defaultDirectory = Path.GetDirectoryName(executable)!;
            CreateShortcut(Path.Combine(startMenu, "Upper.lnk"), executable, "/c echo MixedCase", "");
            CreateShortcut(Path.Combine(startMenu, "Lower.lnk"), executable, "/c echo mixedcase", defaultDirectory);
            CreateShortcut(Path.Combine(startMenu, "Equivalent.lnk"), executable.ToUpperInvariant(),
                "/c echo MixedCase", defaultDirectory.ToUpperInvariant() + Path.DirectorySeparatorChar);
            CreateShortcut(Path.Combine(startMenu, "OtherDirectory.lnk"), executable, "/c echo MixedCase", directory);
            var folders = new Dictionary<DockPinnedApp, string>();
            var variants = DockShortcuts.ReadStartMenuApps(preserveLaunchVariants: true, folders, [startMenu])
                .Where(pin => string.Equals(pin.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase)).ToArray();
            Assert(variants.Length == 3
                && variants.Count(pin => pin.Arguments == "/c echo MixedCase") == 2
                && variants.Count(pin => pin.Arguments == "/c echo mixedcase") == 1,
                "启动变体应保留参数大小写和不同工作目录，并合并等效的 Windows 路径与默认工作目录");
            Assert(variants.All(pin => folders.GetValueOrDefault(pin) == "StartMenu"),
                "去重后的启动变体应保留开始菜单文件夹信息");
            var legacy = DockShortcuts.ReadStartMenuApps(preserveLaunchVariants: false, startMenuRoots: [startMenu]);
            Assert(legacy.Count(pin => string.Equals(pin.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase)) == 1,
                "Dock 的默认开始菜单列表应继续只按程序目标去重");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void CreateShortcut(string path, string target, string arguments, string workingDirectory)
    {
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try
        {
            dynamic link = shortcut = ((dynamic)shell).CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.WorkingDirectory = workingDirectory;
            link.Save();
        }
        finally
        {
            if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    internal static void NormalizeSupplementalEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BingLanLauncherSupplemental", Path.GetRandomFileName());
        var startMenu = Directory.CreateDirectory(Path.Combine(directory, "StartMenu")).FullName;
        var groupFolder = Directory.CreateDirectory(Path.Combine(startMenu, "GroupFolder")).FullName;
        try
        {
            var executable = Environment.ProcessPath!;
            var appShortcut = Path.Combine(startMenu, "App.lnk");
            var uninstallShortcut = Path.Combine(startMenu, "卸载App.lnk");
            CreateShortcut(appShortcut, executable, "", "");
            CreateShortcut(uninstallShortcut, executable, "", "");
            var plainFile = Path.Combine(directory, "Plain.txt");
            File.WriteAllText(plainFile, "plain");

            var input = new[]
            {
                new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.Folder, "GroupFolder", "GroupFolder", groupFolder), "Windows 索引"),
                new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File, "App.lnk", "App.lnk", appShortcut), "Windows 索引"),
                new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File, "卸载App.lnk", "卸载App.lnk", uninstallShortcut), "Windows 索引"),
                new LauncherPanelEntry(new TopBarSearchCandidate(TopBarSearchItemKind.File, "Plain.txt", "Plain.txt", plainFile), "Windows 索引")
            };

            var normalized = LauncherSupplementalNormalizer.Normalize(input, [startMenu]);
            Assert(normalized.Count == 2, $"应只保留应用与普通文件 2 项，实际 {normalized.Count} 项");
            Assert(normalized[0].Candidate.Kind == TopBarSearchItemKind.App
                && string.Equals(normalized[0].Pinned?.ExecutablePath, executable, StringComparison.OrdinalIgnoreCase)
                && string.Equals(normalized[0].IconSource, appShortcut, StringComparison.OrdinalIgnoreCase),
                "补充快捷方式应归一为应用入口并保留 lnk 图标来源");
            Assert(normalized[1].Candidate.Kind == TopBarSearchItemKind.File
                && string.Equals(normalized[1].Candidate.Path, plainFile, StringComparison.OrdinalIgnoreCase),
                "普通文件应原样保留");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static void LaunchMissingTargets(Action<Func<bool>, string> pumpUntil)
    {
        var missing = Path.Combine(Path.GetTempPath(), "BingLanMissingDockTarget", Guid.NewGuid().ToString("N"));
        var pathPin = DockPinRules.CreateFromPath(missing)!;
        var pathLaunch = WindowCommandService.RevealOrLaunchAsync(pathPin);
        pumpUntil(() => pathLaunch.IsCompleted, "不存在的文件固定项应结束后台检查");
        Assert(!pathLaunch.Result.Succeeded && pathLaunch.Result.Message == "文件或文件夹已不存在",
            "文件固定项的后台检查应保留原有失败提示");

        var appLaunch = WindowCommandService.LaunchAsync(new DockPinnedApp
        {
            DisplayName = "missing-app",
            ExecutablePath = missing + ".exe"
        });
        pumpUntil(() => appLaunch.IsCompleted, "不存在的程序固定项应结束后台检查");
        Assert(!appLaunch.Result.Succeeded && appLaunch.Result.Message == "找不到 missing-app 的程序文件",
            "程序固定项的后台检查应保留原有失败提示");
    }

    private static IReadOnlyList<T> ReadOffCallerThread<T>(
        Func<IEnumerable<string>, Task<IReadOnlyList<T>>> read,
        IReadOnlyList<string> paths)
    {
        var callerThread = Environment.CurrentManagedThreadId;
        IEnumerable<string> Enumerate()
        {
            Assert(Environment.CurrentManagedThreadId != callerThread, "磁盘检查和快捷方式解析必须在后台枚举路径");
            foreach (var path in paths) yield return path;
        }
        return read(Enumerate()).GetAwaiter().GetResult();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
