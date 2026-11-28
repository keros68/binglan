using BingLan.App.Tray;
using System.Windows;

internal static class TrayContextOwnerMatchingTests
{
    internal static void Run()
    {
        BluetoothContextMenuOwner();
        GuidMenuObservation();
        foreach (var tooltip in new[]
                 {
                     "网易云音乐 海底（Live） - 凤凰传奇", "网易云音乐：正在播放",
                     "网易云音乐 - 正在播放", "网易云音乐（正在播放）"
                 })
            Check(TrayIconArtwork.IsContextMenuTooltipPrefix(tooltip, "网易云音乐"),
                "本地化产品名后的分隔符应允许动态托盘提示");
        Check(TrayIconArtwork.IsContextMenuTooltipPrefix("任务管理器 CPU 25%\r\n内存 63%", "任务管理器"),
            "动态状态应只按第一行中的完整产品名判断");
        foreach (var (current, registered) in new[]
                 {
                     ("网易云音乐助手", "网易云音乐"), ("其他 网易云音乐 状态", "网易云音乐"),
                     ("网易云音乐", "网易云音乐"), ("网易云音乐 状态", ""),
                     ("微信 未读消息", "微信"), ("其他\n网易云音乐 状态", "网易云音乐")
                 })
            Check(!TrayIconArtwork.IsContextMenuTooltipPrefix(current, registered),
                "前缀回退应拒绝词中前缀、短标签、空标签和后续行");

        var owner = new TrayRegisteredIcon((nint)100, 101, 1234, Guid.Empty, Rect.Empty);
        var other = new TrayRegisteredIcon((nint)200, 201, 1234, Guid.Empty, Rect.Empty);
        var guidOnly = new TrayRegisteredIcon(0, 0, 0, Guid.NewGuid(), Rect.Empty);
        Check(TrayIconArtwork.SelectContextMenuOwner([owner, owner]) == owner,
            "相同真实注册的重复缓存应仍可确认唯一归属");
        Check(TrayIconArtwork.SelectContextMenuOwner([owner, other]) is null,
            "多个真实注册匹配时必须拒绝歧义");
        Check(TrayIconArtwork.SelectContextMenuOwner([guidOnly]) is null
            && TrayIconArtwork.SelectContextMenuOwner([owner with { Owner = 0 }]) is null
            && TrayIconArtwork.SelectContextMenuOwner([owner with { ProcessId = 0 }]) is null,
            "GUID 或单独的进程信息不能替代真实 HWND 与 PID 注册");
    }

    private static void GuidMenuObservation()
    {
        var guid = Guid.NewGuid();
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        string PackagePath(string package) => System.IO.Path.Combine(root, package, "app", "ChatGPT.exe");
        var currentPath = PackagePath("OpenAI.Codex_26.1120.6357.0_x64__2p2nqsd0c76g0");
        var nextPath = PackagePath("OpenAI.Codex_27.1.2.3_x64__2p2nqsd0c76g0");
        Check(TrayIconArtwork.IsGuidMenuObservationIdentity(currentPath, guid)
            && TrayIconArtwork.IsGuidMenuObservationIdentity(nextPath, guid),
            "GUID 观察身份应使用完整包路径并允许同一已核实包的版本更新");
        foreach (var path in new[]
                 {
                     @"C:\Other\ChatGPT.exe", PackagePath("Other_26.1120.6357.0_x64__2p2nqsd0c76g0"),
                     PackagePath("OpenAI.Codex_26.1120.6357.0_x64__other"),
                     PackagePath("OpenAI.Codex_invalid_x64__2p2nqsd0c76g0"),
                     PackagePath("OpenAI.Codex_26.1120_x64__2p2nqsd0c76g0")
                 })
            Check(!TrayIconArtwork.IsGuidMenuObservationIdentity(path, guid),
                "文件名相同或未知包路径不能授权 GUID 菜单观察");
        Check(!TrayIconArtwork.IsGuidMenuObservationIdentity(currentPath, Guid.Empty),
            "空 GUID 必须拒绝观察身份");
        var tile = new Rect(1968, 1598, 48, 72);
        var candidate = (guid, currentPath, tile);
        var owner = TrayIconArtwork.SelectGuidMenuObservation(tile, true, [candidate], [currentPath]);
        Check(owner is { ObservationExecutablePath: not null } && owner.Window == 0 && owner.ProcessId == 0
            && owner.ObservationExecutablePath == currentPath && owner.ObservationIconGuid == guid
            && owner.ObservationTrayBounds == tile,
            "唯一 GUID 和实际运行路径只能产生无 callback HWND/PID 的观察身份");
        Check(TrayIconArtwork.SelectGuidMenuObservation(tile, false, [candidate], [currentPath]) is null
            && TrayIconArtwork.SelectGuidMenuObservation(Rect.Empty, true, [candidate], [currentPath]) is null,
            "无独占 taskbar UIA 物理矩形时不得推断 GUID 观察身份");
        Check(TrayIconArtwork.SelectGuidMenuObservation(tile, true, [candidate], [nextPath]) is null,
            "历史版本缓存不能匹配当前另一版本的运行路径");
        Check(TrayIconArtwork.SelectGuidMenuObservation(tile, true,
                [candidate, (Guid.NewGuid(), currentPath, tile)], [currentPath]) is null
            && TrayIconArtwork.SelectGuidMenuObservation(tile, true,
                [candidate, (Guid.NewGuid(), @"C:\Other\Other.exe", tile)], [currentPath, @"C:\Other\Other.exe"]) is null
            && TrayIconArtwork.SelectGuidMenuObservation(tile, true,
                [(guid, currentPath, new Rect(2000, 1598, 48, 72))], [currentPath]) is null,
            "共享候选矩形或不相符矩形必须拒绝观察身份");
        var otherInstallationGuid = Guid.NewGuid();
        var otherOwner = TrayIconArtwork.SelectGuidMenuObservation(tile, true,
            [(otherInstallationGuid, currentPath, tile)], [currentPath]);
        Check(otherOwner?.ObservationIconGuid == otherInstallationGuid
            && TrayIconArtwork.SelectGuidMenuObservation(tile, true, [], [currentPath]) is null
            && TrayIconArtwork.SelectGuidMenuObservation(tile, true,
                [(otherInstallationGuid, currentPath, new Rect(2000, 1598, 48, 72))], [currentPath]) is null,
            "同一已核实包的另一安装可使用不同非空 GUID，但仍须关联唯一实时同矩形候选");
        Check(!TrayIconArtwork.IsCurrentGuidMenuObservation(new TrayContextMenuOwner(0, 0,
                ObservationExecutablePath: currentPath, ObservationIconGuid: Guid.Empty, ObservationTrayBounds: tile))
            && !TrayIconArtwork.IsCurrentGuidMenuObservation(new TrayContextMenuOwner((nint)100, 101,
                ObservationExecutablePath: currentPath, ObservationIconGuid: guid, ObservationTrayBounds: tile))
            && !TrayIconArtwork.IsCurrentGuidMenuObservation(new TrayContextMenuOwner(0, 0,
                ObservationExecutablePath: currentPath, ObservationIconGuid: guid))
            && !TrayIconArtwork.IsCurrentGuidMenuObservation(new TrayContextMenuOwner(0, 0,
                ObservationExecutablePath: currentPath, ObservationIconGuid: guid,
                ObservationTrayBounds: new Rect(0, 0, 0, 72))),
            "观察复核须在调用 SNI 前拒绝空 GUID、callback 身份和缺少来源矩形");
    }

    private static void BluetoothContextMenuOwner()
    {
        const string ownerClass = "BluetoothNotificationAreaIconWindowClass";
        var window = ((nint)100, 101u, ownerClass);
        foreach (var label in new[] { "蓝牙设备", "  蓝牙设备\t", "Bluetooth Devices", " bluetooth devices " })
        {
            var owner = TrayIconArtwork.SelectBluetoothContextMenuOwner(label, 101, 101, [window, window]);
            Check(owner is { ProcessId: 101, StandardMenusOnly: true } && owner.Window == (nint)100,
                "蓝牙精确标签与当前 Explorer 的唯一组件 HWND 应仅授权标准菜单观察");
        }
        foreach (var label in new[] { "蓝牙设备助手", "Bluetooth Device", "Bluetooth Devices status", "其他\n蓝牙设备" })
            Check(TrayIconArtwork.SelectBluetoothContextMenuOwner(label, 101, 101, [window]) is null,
                "蓝牙观察适配不得认领其他标签或第一行之后的名称");
        Check(TrayIconArtwork.SelectBluetoothContextMenuOwner("蓝牙设备", 101, 102, [window]) is null
            && TrayIconArtwork.SelectBluetoothContextMenuOwner("蓝牙设备", 0, 0, [window]) is null,
            "非当前 Explorer 或缺少 Shell 信息时不得认领蓝牙组件");
        Check(TrayIconArtwork.SelectBluetoothContextMenuOwner("蓝牙设备", 101, 101,
                [window, ((nint)200, 101u, ownerClass)]) is null,
            "同一 Explorer 内多个蓝牙组件 HWND 必须拒绝歧义");
        Check(TrayIconArtwork.SelectBluetoothContextMenuOwner("蓝牙设备", 101, 101,
                [((nint)100, 102u, ownerClass), ((nint)200, 101u, "OtherTrayClass"), (0, 101u, ownerClass)]) is null,
            "其他进程、其他窗口类和空句柄不能构成蓝牙菜单所有者");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
