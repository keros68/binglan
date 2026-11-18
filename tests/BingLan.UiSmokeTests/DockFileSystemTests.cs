using System.IO;
using System.Runtime.InteropServices;
using BingLan.App.Dock;
using BingLan.Core.Dock;
using BingLan.Core.Models;

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
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            object? shortcut = null;
            try
            {
                dynamic link = shortcut = ((dynamic)shell).CreateShortcut(shortcutPath);
                link.TargetPath = executable;
                link.Arguments = "/c exit 0";
                link.WorkingDirectory = directory;
                link.Save();
            }
            finally
            {
                if (shortcut is not null) Marshal.FinalReleaseComObject(shortcut);
                Marshal.FinalReleaseComObject(shell);
            }

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
