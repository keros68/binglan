using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.SmokeTests;

/// <summary>
/// Core-level coverage for the shell-entry/missing-path rules (FILE-05, FILE-09) and the
/// relink flow, kept in its own file per AGENTS.md to minimise merge conflicts with the
/// top-level test script.
/// </summary>
internal static class FileBoxRuleTests
{
    public static void Run()
    {
        TestShellEntryAndMissingRules();
        TestCollectGoneIdsAndParentDirectories();
        TestAddSystemEntryDedupe();
        TestRelinkKeepsIdAndOrder();
        TestRelinkRejectsShellEntriesAndMissingTargets();
        TestRenameChangesOnlyTheDisplayName();
        TestDisplayNameHidesLauncherSuffixes();
        TestTransferMovesMappingBetweenBoxes();
        TestDistributeHonoursCustomBoxes();
        TestViewModePersistenceAndMigration();
        TestDeleteOriginalRules();
        TestDeleteOriginalSettingDefaultsOff();
        TestHeaderToolsOnHoverDefaultsOff();
    }

    // 标题栏按钮悬停显示是选择开启的外观偏好：新状态与 v37 升级后都默认关闭，开启后保存重载保留。
    private static void TestHeaderToolsOnHoverDefaultsOff()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-header-tools-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LocalStateStore(temp);
            Assert(!LocalStateStore.CreateDefault().HeaderToolsOnHover, "新安装应默认一直显示标题栏按钮");
            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 37, "HeaderToolsOnHover": true }""");
            var upgraded = store.Load();
            Assert(upgraded.SchemaVersion == AppState.CurrentSchemaVersion && !upgraded.HeaderToolsOnHover,
                "v37 迁移后标题栏按钮应保持一直显示");
            upgraded.HeaderToolsOnHover = true;
            store.Save(upgraded);
            Assert(store.Load().HeaderToolsOnHover, "开启悬停显示后保存重载应保留");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    // 删除原文件只允许本地固定磁盘上的普通项目；系统入口、失效项、磁盘根目录、系统文件夹及其上级一律拒绝。
    private static void TestDeleteOriginalRules()
    {
        var guarded = new[] { @"C:\Users\me", @"C:\Users\me\Desktop", @"C:\Windows" };
        Assert(OriginalFileDeleteRules.Check(@"C:\Users\me\Desktop\报告.docx", true, true, guarded) == OriginalFileDeleteBlock.None,
            "桌面上的普通文件应允许移到回收站");
        Assert(OriginalFileDeleteRules.Check(@"D:\资料\项目", true, true, guarded) == OriginalFileDeleteBlock.None,
            "普通文件夹应允许移到回收站");
        Assert(OriginalFileDeleteRules.Check("shell:RecycleBinFolder", true, true, guarded) == OriginalFileDeleteBlock.ShellEntry,
            "系统入口不能删除");
        Assert(OriginalFileDeleteRules.Check(@"C:\Users\me\Desktop\已删.txt", false, true, guarded) == OriginalFileDeleteBlock.Missing,
            "原文件不存在时不能删除");
        Assert(OriginalFileDeleteRules.Check(@"D:\", true, true, guarded) == OriginalFileDeleteBlock.ProtectedLocation,
            "磁盘根目录不能删除");
        Assert(OriginalFileDeleteRules.Check(@"C:\Users\me\Desktop\", true, true, guarded) == OriginalFileDeleteBlock.ProtectedLocation,
            "桌面文件夹本身不能删除");
        Assert(OriginalFileDeleteRules.Check(@"c:\windows", true, true, guarded) == OriginalFileDeleteBlock.ProtectedLocation,
            "系统文件夹不能删除（不区分大小写）");
        Assert(OriginalFileDeleteRules.Check(@"C:\Users", true, true, guarded) == OriginalFileDeleteBlock.ProtectedLocation,
            "包含系统文件夹的上级目录不能删除");
        Assert(OriginalFileDeleteRules.Check(@"C:\Users\me2", true, true, guarded) == OriginalFileDeleteBlock.None,
            "名称前缀相同的兄弟目录不应被误判为受保护");
        Assert(OriginalFileDeleteRules.Check(@"E:\U盘\照片.jpg", true, false, guarded) == OriginalFileDeleteBlock.NotRecyclable,
            "无法放入回收站的位置应拒绝，而不是永久删除");
    }

    // 删除原文件是选择开启的功能：新状态与旧版本升级后都默认关闭，开启后保存重载保留。
    private static void TestDeleteOriginalSettingDefaultsOff()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-delete-originals-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LocalStateStore(temp);
            Assert(!LocalStateStore.CreateDefault().FileBoxDeleteOriginalsEnabled, "新安装应默认关闭删除原文件");
            File.WriteAllText(store.StatePath, """{ "SchemaVersion": 36, "FileBoxDeleteOriginalsEnabled": true }""");
            var upgraded = store.Load();
            Assert(upgraded.SchemaVersion == AppState.CurrentSchemaVersion && !upgraded.FileBoxDeleteOriginalsEnabled,
                "v36 迁移后删除原文件应保持关闭");
            upgraded.FileBoxDeleteOriginalsEnabled = true;
            store.Save(upgraded);
            Assert(store.Load().FileBoxDeleteOriginalsEnabled, "开启删除原文件后保存重载应保留");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    // 分组盒图标/列表视图只改变显示，不触碰映射；旧状态文件升级后保持图标视图。
    private static void TestViewModePersistenceAndMigration()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-viewmode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var store = new LocalStateStore(temp);
            File.WriteAllText(
                store.StatePath,
                """{ "SchemaVersion": 22, "FileBoxes": [ { "Title": "旧盒" } ] }""");
            var upgraded = store.Load();
            Assert(
                upgraded.SchemaVersion == AppState.CurrentSchemaVersion
                    && upgraded.FileBoxes.Single().ViewMode == FileBoxViewMode.Tiles,
                "Schema v22 状态升级后分组盒默认图标视图");

            var box = new FileBoxState { ViewMode = FileBoxViewMode.List };
            store.Save(new AppState { FileBoxes = [box] });
            Assert(
                store.Load().FileBoxes.Single().ViewMode == FileBoxViewMode.List,
                "列表视图经保存重载后保持");
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static void TestCollectGoneIdsAndParentDirectories()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-collect-gone-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var keptFile = Path.Combine(temp, "保留.txt");
            var goneFile = Path.Combine(temp, "已删除.txt");
            File.WriteAllText(keptFile, "内容");
            File.WriteAllText(goneFile, "占位");

            var box = new FileBoxState();
            FileMappingService.AddExisting(box, [keptFile, goneFile]);
            FileMappingService.AddSystemEntry(box, "shell:RecycleBinFolder", "回收站");
            var keptId = box.Items.Single(item => item.Path == keptFile).Id;
            var goneId = box.Items.Single(item => item.Path == goneFile).Id;
            File.Delete(goneFile);

            var goneIds = FileMappingService.CollectGoneIds(box.Items);
            Assert(goneIds.Contains(goneId), "原文件已删除的映射应进入待清除集合");
            Assert(!goneIds.Contains(keptId), "原文件存在的映射不应进入待清除集合");
            Assert(goneIds.Count == 1, "系统入口不应进入待清除集合");

            var parents = FileMappingService.CollectExistingParentDirectories(
                box.Items.Select(item => item.Path));
            Assert(
                parents.Count == 1 && parents.Contains(temp),
                "监听目录应只包含现存映射的父目录，系统入口被跳过");

            Assert(
                !FileMappingService.CollectGoneIds(
                        [new FileMappingState { Path = @"\\binglan-offline-host.invalid\share\报告.docx" }])
                    .Any(),
                "离线网络共享上的映射不应进入待清除集合");

            Assert(
                FileMappingService.IsPartialDownload(Path.Combine(temp, "视频.crdownload")),
                "下载中的临时文件应识别为部分下载");
            Assert(
                FileMappingService.IsPartialDownload(Path.Combine(temp, "压缩包.PART")),
                "部分下载识别应忽略扩展名大小写");
            Assert(!FileMappingService.IsPartialDownload(keptFile), "普通文件不应识别为部分下载");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void TestDistributeHonoursCustomBoxes()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-distribute-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            string Make(string name)
            {
                var path = Path.Combine(temp, name);
                File.WriteAllText(path, "x");
                return path;
            }
            var pdf = Make("报告.PDF");
            var photo = Make("照片.jpg");
            var shortcut = Make("浏览器.lnk");
            var other = Make("模型.dwg");
            var folder = Path.Combine(temp, "资料");
            Directory.CreateDirectory(folder);

            Assert(
                FileMappingService.NormalizeExtensions(["pdf, .DOCX  txt", " ", ".pdf"])
                    .SequenceEqual([".pdf", ".docx", ".txt"]),
                "扩展名应统一为小写、带点、去重");

            var documents = new FileBoxState { Title = "文档", CollectExtensions = [".pdf"] };
            var images = new FileBoxState { Title = "图片", CollectExtensions = [".jpg"], CollectFolders = true };
            var duplicateImages = new FileBoxState { Title = "照片", CollectExtensions = [".jpg"] };
            var apps = new FileBoxState { Title = "软件", DesktopCategory = DesktopGroupCategory.Applications };
            var result = FileMappingService.Distribute(
                [pdf, photo, shortcut, other, folder],
                [documents, images, duplicateImages, apps]);

            Assert(result.ByBox[documents.Id].SequenceEqual([pdf]), "文档盒应收纳 PDF，扩展名不分大小写");
            Assert(result.ByBox[images.Id].Contains(photo) && result.ByBox[images.Id].Contains(folder),
                "图片盒应收纳图片和勾选的文件夹");
            Assert(!result.ByBox.ContainsKey(duplicateImages.Id), "同一类型应放进列表中靠前的盒子");
            Assert(result.ByBox[apps.Id].SequenceEqual([shortcut]), "没有自定义盒收纳的快捷方式应进原来的软件盒");
            Assert(
                result.Unclaimed.Count == 1 && result.Unclaimed[DesktopGroupCategory.Files].SequenceEqual([other]),
                "没有盒子收纳的文件应留给新建的文件分类盒");
            Assert(File.Exists(pdf) && Directory.Exists(folder), "分配不得移动原文件");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void TestRenameChangesOnlyTheDisplayName()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-rename-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var first = Path.Combine(temp, "b-报告.txt");
            var second = Path.Combine(temp, "c-预算.txt");
            File.WriteAllText(first, "一");
            File.WriteAllText(second, "二");
            var box = new FileBoxState();
            FileMappingService.AddExisting(box, [first, second]);
            FileMappingService.AddSystemEntry(box, "shell:RecycleBinFolder", "回收站");
            var item = box.Items[1];

            Assert(FileMappingService.Rename(box, item.Id, "  A 预算表  "), "修改显示名称应成功");
            Assert(item.Title == "A 预算表" && item.Path == second, "显示名称应去除首尾空白且不改变路径");
            Assert(File.Exists(second) && File.ReadAllText(second) == "二", "修改显示名称不得触碰原文件");
            FileMappingService.Sort(box, FileMappingSortMode.Name);
            Assert(
                item.Order < box.Items.Single(x => x.Path == first).Order,
                "按名称排序应使用显示名称");

            Assert(FileMappingService.Rename(box, item.Id, " "), "留空应恢复原名称");
            Assert(item.Title is null, "留空后不应保留显示名称");
            var shell = box.Items.Single(x => FileMappingService.IsShellEntry(x.Path));
            Assert(!FileMappingService.Rename(box, shell.Id, ""), "系统入口没有文件名，不能清空显示名称");
            Assert(shell.Title == "回收站", "拒绝清空后系统入口应保留原名称");
            Assert(!FileMappingService.Rename(box, Guid.NewGuid(), "不存在"), "不存在的映射不应改名");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    // 自动显示名隐藏快捷方式/程序后缀（与资源管理器隐藏已知扩展名一致），文档后缀保留。
    private static void TestDisplayNameHidesLauncherSuffixes()
    {
        Assert(
            FileMappingService.GetDisplayName(new FileMappingState { Path = @"C:\Users\冰蓝\桌面\浏览器.lnk" }) == "浏览器",
            "快捷方式的显示名称应隐藏 .lnk 后缀");
        Assert(
            FileMappingService.GetDisplayName(new FileMappingState { Path = @"C:\Users\冰蓝\桌面\必应.URL" }) == "必应",
            "后缀隐藏应忽略大小写");
        Assert(
            FileMappingService.GetDisplayName(new FileMappingState { Path = @"C:\工具\自启动.appref-ms" }) == "自启动",
            "程序与脚本后缀应与快捷方式同样隐藏");
        Assert(
            FileMappingService.GetDisplayName(new FileMappingState { Path = @"C:\Users\冰蓝\桌面\报告.docx" }) == "报告.docx",
            "文档后缀是区分信息，应保留");
        Assert(
            FileMappingService.GetDisplayName(new FileMappingState { Path = @"C:\Users\冰蓝\桌面\.lnk" }) == ".lnk",
            "文件名只剩后缀时应保留完整名称");
        Assert(
            FileMappingService.GetDisplayName(new FileMappingState { Path = @"C:\Users\冰蓝\桌面\浏览器.lnk", Title = "  " }) == "浏览器",
            "空白标题应回退为剥后缀的文件名");
        Assert(
            FileMappingService.GetDisplayName(new FileMappingState { Path = @"C:\Users\冰蓝\桌面\浏览器.lnk", Title = "网上冲浪" }) == "网上冲浪",
            "自定义标题应优先于文件名");
    }

    private static void TestTransferMovesMappingBetweenBoxes()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-transfer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var first = Path.Combine(temp, "一.txt");
            var second = Path.Combine(temp, "二.txt");
            File.WriteAllText(first, "一");
            File.WriteAllText(second, "二");
            var source = new FileBoxState();
            var target = new FileBoxState();
            FileMappingService.AddExisting(source, [first, second]);
            FileMappingService.AddExisting(target, [second]);
            var moved = source.Items[0];
            FileMappingService.Rename(source, moved.Id, "第一份");

            Assert(FileMappingService.Transfer(source, target, moved.Id), "转移到另一个分组盒应成功");
            Assert(
                source.Items.Count == 1 && source.Items[0].Order == 0 &&
                target.Items.Count == 2 && target.Items[1].Id == moved.Id && target.Items[1].Order == 1,
                "转移后映射应从原盒移除并追加到目标盒末尾");
            Assert(moved.Title == "第一份" && moved.Path == first, "转移应保留显示名称和路径");
            Assert(File.Exists(first) && File.Exists(second), "转移映射不得移动原文件");

            var duplicate = source.Items[0];
            Assert(!FileMappingService.Transfer(source, target, duplicate.Id), "目标盒已有同一路径时不应转移");
            Assert(source.Items.Count == 1 && target.Items.Count == 2, "被拒绝的转移不应改变任何一个盒子");
            Assert(!FileMappingService.Transfer(source, source, duplicate.Id), "不应转移到同一个盒子");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void TestShellEntryAndMissingRules()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-shell-rules-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var file = Path.Combine(temp, "示例.txt");
            File.WriteAllText(file, "内容");
            var missing = Path.Combine(temp, "不存在.txt");

            Assert(FileMappingService.IsShellEntry("shell:RecycleBinFolder"), "shell: 前缀应识别为系统入口");
            Assert(FileMappingService.IsShellEntry("SHELL:MyComputerFolder"), "系统入口识别应忽略大小写");
            Assert(!FileMappingService.IsShellEntry(file), "普通路径不应识别为系统入口");

            Assert(!FileMappingService.IsMissing("shell:RecycleBinFolder"), "系统入口不应被判定为已失效");
            Assert(!FileMappingService.IsMissing(file), "存在的文件不应被判定为已失效");
            Assert(FileMappingService.IsMissing(missing), "不存在的路径应被判定为已失效");

            var mapping = new FileMappingState { Path = missing };
            Assert(FileMappingService.IsMissing(mapping), "映射重载应与路径判定一致");

            Assert(FileMappingService.IsGone(missing), "所在磁盘可访问但路径不存在应判定为已删除");
            Assert(!FileMappingService.IsGone(file), "存在的文件不应判定为已删除");
            Assert(!FileMappingService.IsGone("shell:RecycleBinFolder"), "系统入口不应判定为已删除");
            var offlineRoot = Enumerable.Range('D', 23)
                .Select(letter => $"{(char)letter}:\\")
                .FirstOrDefault(root => !Directory.Exists(root));
            if (offlineRoot is not null)
            {
                Assert(
                    !FileMappingService.IsGone(Path.Combine(offlineRoot, "资料", "报告.docx")),
                    "未连接磁盘上的路径应保留映射");
            }
            Assert(
                !FileMappingService.IsGone(@"\\binglan-offline-host.invalid\share\报告.docx"),
                "离线网络共享上的路径应保留映射");

            var box = new FileBoxState();
            FileMappingService.AddExisting(box, [file]);
            var kept = box.Items[0];
            var gone = new FileMappingState { Path = missing, Order = 1 };
            box.Items.Add(gone);
            Assert(FileMappingService.RemoveGone(box, new HashSet<Guid> { gone.Id }) == 1, "应清除一项已删除映射");
            Assert(box.Items.Count == 1 && box.Items[0].Id == kept.Id, "清除后应只保留有效映射");
            Assert(File.Exists(file), "清除映射不应影响原文件");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void TestAddSystemEntryDedupe()
    {
        var box = new FileBoxState();
        Assert(
            FileMappingService.AddSystemEntry(box, "shell:RecycleBinFolder", "回收站") == 1,
            "首次添加系统入口应成功");
        Assert(
            FileMappingService.AddSystemEntry(box, "shell:RecycleBinFolder", "回收站") == 0,
            "重复添加同一系统入口不应产生第二条映射");
        Assert(box.Items.Count == 1, "重复添加不应改变映射总数");
        Assert(box.Items[0].Title == "回收站", "系统入口应保存自定义标题");

        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-system-entry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            Assert(
                FileMappingService.AddSystemEntry(box, temp, "下载") == 1,
                "真实已知文件夹路径也应能作为系统入口添加");
            Assert(
                FileMappingService.AddSystemEntry(box, Path.Combine(temp, "缺失"), "下载") == 0,
                "不存在的真实路径不应作为系统入口添加");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void TestRelinkKeepsIdAndOrder()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-relink-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var first = Path.Combine(temp, "第一项.txt");
            var missing = Path.Combine(temp, "已删除.txt");
            var replacement = Path.Combine(temp, "替换目标.txt");
            File.WriteAllText(first, "第一项");
            File.WriteAllText(missing, "占位");
            File.WriteAllText(replacement, "替换内容");

            var box = new FileBoxState();
            FileMappingService.AddExisting(box, [first, missing]);
            File.Delete(missing);

            var target = box.Items.Single(item => item.Path == missing);
            var originalId = target.Id;
            var originalOrder = target.Order;

            Assert(
                FileMappingService.Relink(box, originalId, replacement),
                "重新定位到有效目标应成功");
            Assert(target.Id == originalId, "重新定位不应改变映射 Id");
            Assert(target.Order == originalOrder, "重新定位不应改变映射顺序");
            Assert(target.Path == Path.GetFullPath(replacement), "重新定位应写入新路径");
            Assert(target.Title is null, "重新定位后应清空旧标题以便按新路径显示名称");
            Assert(File.Exists(missing) is false && File.Exists(replacement), "重新定位不应触碰任何文件");
            Assert(File.Exists(first), "重新定位不应影响其它映射对应的文件");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void TestRelinkRejectsShellEntriesAndMissingTargets()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"BingLan-relink-reject-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            var existing = Path.Combine(temp, "存在.txt");
            File.WriteAllText(existing, "内容");

            var box = new FileBoxState();
            FileMappingService.AddSystemEntry(box, "shell:RecycleBinFolder", "回收站");
            var shellItem = box.Items.Single();
            Assert(
                !FileMappingService.Relink(box, shellItem.Id, existing),
                "系统入口不应支持重新定位");
            Assert(shellItem.Path == "shell:RecycleBinFolder", "被拒绝的重新定位不应修改系统入口路径");

            FileMappingService.AddExisting(box, [existing]);
            var normalItem = box.Items.Single(item => item.Path == existing);
            Assert(
                !FileMappingService.Relink(box, normalItem.Id, Path.Combine(temp, "不存在的目标.txt")),
                "重新定位目标不存在时应失败");
            Assert(
                !FileMappingService.Relink(box, Guid.NewGuid(), existing),
                "找不到映射 Id 时重新定位应失败");
        }
        finally
        {
            Directory.Delete(temp, true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
