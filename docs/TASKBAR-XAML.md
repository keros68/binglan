# 任务栏 XAML 适配

透明模式由 WPF 宿主和一个自有 C++ DLL 组成，不增加独立常驻程序。当前启用范围为 Windows 11 x64 Build 26200 与 26300，且要求非高对比度/远程会话、没有其他任务栏工具；系统透明效果关闭时由宿主自动开启（写入注册表前在检查点记录原值，恢复时一并还原），用户在开启期间手动关闭透明效果时宿主让出。其他 Windows 11 Build 改用模糊代替透明，不加载 XAML 组件。Windows 10 不使用本组件：任务栏是 Win32 窗口，透明由窗口材质实现，见 [任务栏验证](TASKBAR-QA.md)。

## 修改范围

`BingLan.Taskbar.Xaml.dll` 通过微软的 [InitializeXamlDiagnosticsEx](https://learn.microsoft.com/en-us/windows/win32/api/xamlom/nf-xamlom-initializexamldiagnosticsex) 进入当前用户的 Explorer，使用 [IVisualTreeService](https://learn.microsoft.com/en-us/windows/win32/api/xamlom/nn-xamlom-ivisualtreeservice) 接收元素变化。连接前核对任务栏所属进程的完整路径为 Windows 目录中的 `explorer.exe`。

只接受类型为 `Taskbar.TaskbarBackground`、名称为 `BackgroundControl`，且三层父级以内存在 `Taskbar.TaskbarFrame` 的元素。透明时只写该元素的 `Opacity=0`。原来的画刷、资源、布局、按钮、托盘和输入属性保持原值。关闭时恢复原先的局部值；原先没有局部值时清除覆盖值。该内部元素结构不是微软承诺的稳定任务栏接口，所以同时校验 Build 和实际结构。

不使用任意进程加载、远程线程、系统文件替换、脚本或第三方 GPL/AGPL 代码。原生实现由本仓库维护，依赖 Windows SDK 中的 COM 和 C++/WinRT 接口。

## 生命周期与恢复

- 宿主在改变背景前写入 Schema 2 任务栏恢复记录，保存随机会话名称；Schema 1 的窗口材质记录仍可读取。
- 会话使用 32 字节共享状态，包含协议、宿主 PID、启用标志、背景数量、停止/错误状态和心跳。名称限定为 `Local\BingLan.Taskbar.Xaml.` 加随机 GUID，恢复函数拒绝其他名称。
- 宿主每秒续一次心跳。组件持有宿主进程句柄；宿主退出、请求停止或心跳过期 5 秒后，各 XAML 线程在自己的 500 ms 定时器中恢复属性。UI 线程不跨线程读写 XAML 对象。
- 正常切回默认等待恢复确认后删除检查点。异常结束无需等待下次启动就可恢复背景，检查点由恢复命令或下次启动核对后删除。恢复失败保留记录。
- 连接、DLL 缓存和恢复等待放在后台。模式请求串行处理，新请求取消旧请求，先恢复再应用。连接等待最多 10 秒，元素确认最多 5 秒；无法确认时停止。
- Explorer 重建时监听 `TaskbarCreated`，并通过任务栏所属 PID 变化重新连接。此路径已实现，真实重启矩阵尚未执行。
- DLL 以 SHA-256 内容标识复制到本机数据目录的 `Native` 缓存，通过临时文件完成写入并校验内容。Explorer 中已加载的 DLL 保留到 Explorer 退出，以避免 COM 回调卸载竞态；会话停止后解除订阅、停止定时器并结束工作线程。每个加载版本的文件约 189 KiB，更新不覆盖仍被 Explorer 映射的副本。缓存不进入主题包。
- 连接前通过文件句柄解析 DLL 的真实路径，确保从带 AppData 重定向的打包宿主启动时，Explorer 也能读取同一份组件。

## 构建与分发

`BingLan.App.csproj` 增量调用 `src/BingLan.Taskbar.Xaml/build.ps1`，把 DLL 复制到构建和发布输出。开发机需要 Visual Studio 2022 C++ x64 工具链与 Windows SDK，使用现有工具安装；本轮工具版本为 MSVC 14.44.35207、Windows SDK 10.0.26100.0。C/C++ 运行库静态链接，不增加安装用户的运行库安装步骤。

模糊模式继续使用原有的系统窗口材质。透明模式不调用该接口。自动隐藏继续使用 `SHAppBarMessage`，与原生组件独立。

真机结果、截图与限制见 [任务栏验证](TASKBAR-QA.md)。
