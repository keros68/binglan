using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

internal static class FileBoxTransferTests
{
    internal static void Run(Action<Window> show, Action pump)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"BingLan-file-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        FileBoxWindow? source = null;
        FileBoxWindow? target = null;
        try
        {
            var paths = Enumerable.Range(1, 4)
                .Select(index => Path.Combine(directory, $"项目{index}.txt")).ToArray();
            foreach (var path in paths)
            {
                File.WriteAllText(path, $"原文件：{Path.GetFileName(path)}");
            }
            var sourceState = new FileBoxState { Title = "来源" };
            var targetState = new FileBoxState { Title = "目标" };
            FileMappingService.AddExisting(sourceState, paths);
            FileMappingService.AddExisting(targetState, paths.Take(2));
            var first = sourceState.Items[0];
            var second = sourceState.Items[1];
            var moved = sourceState.Items[2];
            var copied = sourceState.Items[3];
            moved.Title = "转移显示名称";
            copied.Title = "复制显示名称";
            var originalTargetIds = targetState.Items.Select(item => item.Id).ToArray();
            source = new FileBoxWindow(sourceState);
            target = new FileBoxWindow(targetState);
            target.OtherBoxes = () => [source];
            show(source);
            show(target);

            source.TransferMappingsByIdsTo([first.Id], target);
            pump();
            Assert(sourceState.Items.Contains(first) && targetState.Items.Count == 2,
                "单项同路径转移应保留源映射，目标不应增加副本");
            Assert(Status(target) == "所选项目已在此收纳盒中",
                "单项重复转移不应显示转入成功");

            source.TransferMappingsByIdsTo([first.Id, second.Id], target);
            pump();
            Assert(sourceState.Items.Count == 4
                && targetState.Items.Select(item => item.Id).SequenceEqual(originalTargetIds),
                "全部重复的批量转移应保留两个盒子的映射");
            Assert(Status(target) == "所选项目已在此收纳盒中",
                "全部重复的批量转移不应显示转入成功");

            var sourceIcons = (FileBoxTabIconCache)typeof(FileBoxWindow)
                .GetField("_tabIconCache", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(source)!;
            sourceIcons.Store(moved, new System.Windows.Media.DrawingImage(), false);
            source.TransferMappingsByIdsTo([first.Id, moved.Id], target);
            Assert(sourceIcons.Get(moved) is null, "跨盒移动后，来源应释放已移出映射的旧图标");
            pump();
            Assert(sourceState.Items.Count == 3 && sourceState.Items.Contains(first)
                && !sourceState.Items.Contains(moved) && targetState.Items.Count == 3,
                "混合批量转移应只移走未重复项，重复项留在来源");
            Assert(ReferenceEquals(targetState.Items.Single(item => item.Id == moved.Id), moved)
                && moved.Title == "转移显示名称",
                "转移应保留映射 Id 和显示名称");
            Assert(Status(target) == "已从“来源”转入 1 项（跳过 1 项重复）",
                "混合批量转移应提示实际转入和重复数量");

            DropWithControl(source, target, [copied.Id]);
            pump();
            var clone = targetState.Items.Single(item => item.Path == copied.Path);
            Assert(sourceState.Items.Contains(copied) && sourceState.Items.Count == 3
                && targetState.Items.Count == 4,
                "Ctrl 拖入应保留来源并在目标新增映射");
            Assert(!ReferenceEquals(clone, copied) && clone.Id != copied.Id
                && clone.Title == copied.Title && clone.Path == copied.Path,
                "Ctrl 拖入应创建新 Id 并保留路径与显示名称");
            Assert(Status(target) == "已从“来源”复制 1 项映射",
                "Ctrl 拖入应显示复制结果");
            clone.Title = "目标独立名称";
            Assert(copied.Title == "复制显示名称", "复制后修改目标名称不应改变来源");

            DropWithControl(source, target, [copied.Id]);
            pump();
            Assert(sourceState.Items.Count == 3 && targetState.Items.Count == 4
                && Status(target) == "所选项目已在此收纳盒中",
                "Ctrl 拖入相同路径应保留来源并拒绝重复映射");
            Assert(Tiles(source).Select(tile => tile.Mapping.Id)
                    .SequenceEqual(sourceState.Items.Select(item => item.Id))
                && Tiles(target).Select(tile => tile.Mapping.Id)
                    .SequenceEqual(targetState.Items.Select(item => item.Id)),
                "转移与复制后的可见项目应与盒子状态一致");
            Assert(sourceState.Items.Select(item => item.Order).SequenceEqual(Enumerable.Range(0, 3))
                && targetState.Items.Select(item => item.Order).SequenceEqual(Enumerable.Range(0, 4)),
                "转移与复制后的映射顺序应连续");
            Assert(Directory.GetFiles(directory).Order().SequenceEqual(paths.Order())
                && paths.All(path => File.ReadAllText(path) == $"原文件：{Path.GetFileName(path)}"),
                "转移和 Ctrl 复制映射不应移动、复制或修改磁盘原文件");
        }
        finally
        {
            if (source is not null)
            {
                source.CanClose = true;
                source.Close();
            }
            if (target is not null)
            {
                target.CanClose = true;
                target.Close();
            }
            pump();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DropWithControl(FileBoxWindow source, FileBoxWindow target, Guid[] ids)
    {
        // WPF does not expose a DragEventArgs constructor; raise the real routed event
        // with its internal constructor so the modifier branch is checked as well.
        var payloadType = typeof(FileBoxWindow).GetNestedType("InternalBoxDragPayload", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("找不到分组盒拖放数据类型");
        var payload = Activator.CreateInstance(payloadType, [source.State.Id, ids])
            ?? throw new InvalidOperationException("无法创建分组盒拖放数据");
        var data = new DataObject();
        data.SetData("BingLan.FileBoxMappings", payload);
        var args = (DragEventArgs?)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: [data, DragDropKeyStates.ControlKey, DragDropEffects.Copy | DragDropEffects.Link, target, new Point()],
            culture: null) ?? throw new InvalidOperationException("无法创建 WPF 拖放事件");
        args.RoutedEvent = DragDrop.DropEvent;
        target.RaiseEvent(args);
    }

    private static string Status(FileBoxWindow window) => ((TextBlock)window.FindName("StatusText")).Text;

    private static IEnumerable<FileBoxWindow.FileTile> Tiles(FileBoxWindow window) =>
        ((ItemsControl)window.FindName("FileList")).Items.Cast<FileBoxWindow.FileTile>();

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
