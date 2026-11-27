# 独立托盘面板实验

.NET 10 / WPF 托盘实验支持独立运行，正式版可从顶栏「托盘」打开同一面板。预览面板随顶栏配色，平时收起，点开后默认一排显示最多 5 个应用，点箭头可展开其余图标。悬停显示应用名称、Windows 提供的状态和所在区域。名称为空时，面板按 Windows 报告的图标位置确认所属应用，显示其名称和图标；无法确认时显示「未命名的托盘图标」。Esc 可关闭面板。顶栏入口启用时在后台准备一份图标列表，打开时先显示已准备好的图标，再向 Windows 核对新增、退出的应用和状态。面板已打开时也可以点刷新。OneDrive 使用蓝色云朵标识，其同步状态取自 Windows 返回的文字。

工具按需读取当前 Explorer 的任务栏和已存在的折叠区，包括隐藏 XAML 子窗口。Explorer 刚重启时，部分隐藏图标要在 Windows 原生折叠区打开一次后才可读取。遇到缺项，可打开原生折叠区，再回冰蓝刷新。图像从本机 Windows 通知区域缓存匹配，缺少图片的应用以名称首字显示。

工具不创建原生折叠区，不改变任务栏、图标固定或自动隐藏设置，不发送物理点击，不启动第三方程序，也不挂接 Explorer。

左键发送默认动作，右键、菜单键或 Shift+F10 请求原应用菜单。右键桥接 DLL 使用 Windows SDK 的公开 `IUIAutomationElement3.ShowContextMenu`，按完整 RuntimeId 重新匹配唯一、启用、同 Explorer 进程的真实托盘项。该 API 可以回退到父级菜单，HRESULT 成功不代表命中了目标图标；原应用的实际响应与任务栏是否保持收起需要现场验证。

从仓库根目录构建，需要 x64 C++ Build Tools 与 Windows SDK；实验 DLL 与 WPF 程序均为 x64：

```powershell
dotnet build .\design-lab\BingLan.TrayPanelSpike -c Release
```

构建带顶栏入口的本地预览，右键顶栏「托盘」可打开 Windows 原生托盘：

```powershell
dotnet build .\src\BingLan.App -c Release --artifacts-path "$env:TEMP\BingLan-TrayHostArtifacts"
```

只读探测，输出 JSON，8 秒内未完成则退出：

```powershell
dotnet run --project .\design-lab\BingLan.TrayPanelSpike -c Release -- --probe
```

打开独立面板；窗口不出现在任务栏，也不置顶：

```powershell
dotnet run --project .\design-lab\BingLan.TrayPanelSpike -c Release
```

注册一个只记录回调的测试图标，打开面板并在 20 秒后自动关闭、删除测试图标：

```powershell
dotnet run --project .\design-lab\BingLan.TrayPanelSpike -c Release -- --own-icon --seconds=20
```

测试名称包含本次进程 ID。控制台分别输出 `NIM_ADD`、`NIM_SETVERSION`、每条原始回调和退出前的 `NIM_DELETE` 结果。版本 4 的一次默认动作可能同时产生鼠标通知和 `NIN_SELECT`；选择计数只累计 `NIN_SELECT` / `NIN_KEYSELECT`，菜单计数只累计 `WM_CONTEXTMENU`。

测试图标收到菜单请求后打开「冰蓝托盘测试菜单」，唯一操作为「关闭测试菜单」，4 秒后自动收回。控制台输出菜单跟踪开始、`WM_ENTERMENULOOP`、`WM_INITMENUPOPUP`、超时与结束消息；关闭实验窗口时也会先收回本菜单。

自动验证自有图标的真实默认动作：

```powershell
dotnet run --project .\design-lab\BingLan.TrayPanelSpike -c Release -- --verify-own
```

该入口没有可见面板，注册测试图标后使用与面板按钮相同的查找和调用路径，仅在该唯一图标的选择回调计数增长时返回 0。注册失败、图标不可访问、调用超时或未收到回调均返回 1。退出前删除测试图标。

验证同一个自有图标的菜单请求与限时收回：

```powershell
dotnet run --project .\design-lab\BingLan.TrayPanelSpike -c Release -- --verify-own-context
```

该入口会短暂显示自有测试菜单。仅在唯一测试图标收到 `WM_CONTEXTMENU`、原生菜单初始化计数增长且菜单已收回时返回 0；截图与任务栏几何需独立确认。该结果不代表其他应用的菜单均可访问。

UIA 提供程序的原生调用无法强制取消。超过等待时间后面板停止继续操作；已经进入提供程序的调用可能仍在处理中，读取超时则不会继续发送迟到动作。此工具没有自动重试或常驻轮询。

现场验收应在原生任务栏已收起时同时记录主、副任务栏实际几何：只读探测、注册测试图标、默认动作、正常关闭各阶段分别确认原生面板未出现且任务栏未展开。UIA 的 `IsOffscreen` 和 `IsWindowVisible` 本身不能证明自动隐藏任务栏的屏幕几何状态。

### Windows 11 双屏实测（2026-11-27）

在 Build 26300.9974、主屏 175%、副屏 150% 下，原生任务栏设置为自动隐藏并禁止触边唤出，测试从两屏各只露出 2 像素开始。

| 操作 | 应用侧结果 | 任务栏 |
|---|---|---|
| 读取既有通知区域 | 读到主区域 7 项、已实例化的折叠区域 6 项。额外注册测试图标后，面板显示 14 项。 | 无需展开原生溢出面板。 |
| 调用折叠区的测试图标 | 连续三轮，每轮均成功注册并删除图标，所属进程收到一次语义选择回调。用鼠标点击实验面板按钮也收到了同样的回调。 | 三轮共 12.06 秒、1,396 次几何采样，两屏可见高度最大均为 2 像素，最大采样间隔 16 毫秒。 |
| 从实验面板右击测试图标 | 所属进程收到菜单请求，并进入原生菜单的跟踪过程，菜单四秒后自动收回。 | 此动作期间两屏仍处于收起状态。 |
| 从实验面板右击主区域的 Everything | 观察到由本机 Everything 进程拥有的可见原生菜单窗口。 | 菜单出现时，主屏任务栏的可见高度由 2 像素增至 84 像素。副屏维持 2 像素，Windows 原生溢出面板仍未显示。 |

独立面板可以操作真实通知图标。Everything 位于常显区时未通过任务栏全程收起的验收。其他 Windows 构建及其他应用的菜单行为仍待验证。

### 重启后用同一个 Everything 图标对照

Windows 11 Build 26300，双屏。Everything 1.4.1.1032 的用户进程和独立面板在三轮测试中保持运行。每轮开始前确认两块屏的任务栏均只露出 2 像素；从同一实验面板右击，并核对可见的原生菜单窗口属于该 Everything 用户进程。

| Windows 中的图标位置 | 次数 | 原生菜单 | 主屏任务栏最大可见高度 | 副屏任务栏最大可见高度 |
|---|---:|---|---:|---:|
| 常显区 | 1 | 打开 | 84 像素 | 2 像素 |
| 折叠区 | 2 | 两次均打开 | 2 像素 | 2 像素 |

两轮折叠区测试共采样 5,198 次，约 47 秒，最长相邻采样间隔 31 毫秒。此结果适用于本机的 Everything。要试用这种布局，在 Windows「设置 → 个性化 → 任务栏 → 其他系统托盘图标」关闭对应应用的常显开关，保留隐藏图标菜单，再在独立面板点击「刷新图标」。图标仍由 Explorer 托管。其他应用和系统版本应分别验证。
