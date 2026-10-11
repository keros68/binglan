# 安装包

## 构建

```powershell
pwsh -NoProfile -File .\installer\build.ps1 -Version 0.2.13
```

输出 `installer\bin\BingLan-Setup-0.2.13.exe`（约 53 MB）和免安装包 `installer\bin\BingLan-0.2.13-portable.zip`。脚本先把应用发布为 win-x64 自包含程序（附带 .NET 运行时，只保留中文和英文资源），再用 Inno Setup 打包为按用户安装的安装程序，并把同一份发布输出压成免安装 zip。构建还需要 .NET 10 SDK、PowerShell 7、Visual Studio 2022 C++ x64 工具链和 Windows SDK；自有任务栏 DLL 随发布输出进入安装包，细节见 [任务栏 XAML 适配](TASKBAR-XAML.md)。需要 Inno Setup 6：`winget install JRSoftware.InnoSetup --scope user`。

## 免安装包

zip 版内容与安装后的程序目录一致（程序文件、`使用说明.md`、`字体许可`），解压到任意文件夹后运行其中的 `BingLan.exe` 即可，不需要管理员权限，不创建快捷方式。个人数据与安装版一样保存在 `%LOCALAPPDATA%\BingLanWidgets`，两种版本可互换使用、配置不丢；单实例门禁决定了免安装版和已安装版不能同时运行。应用内的“下载并安装”启动的是安装程序，会把免安装使用变成安装版；免安装版更新时到 Releases 重新下载、解压覆盖。没有卸载流程：删除文件夹前先退出程序，若开启了“登录时启动”先在设置里关闭；任务栏或桌面图标未恢复时运行一次 `BingLan.exe --restore-taskbar`，彻底清理个人数据时删除 `%LOCALAPPDATA%\BingLanWidgets`。

## 安装程序行为

- 安装到 `%LOCALAPPDATA%\Programs\BingLan`，不需要管理员权限，不写入 `Program Files` 或 `HKLM`；要求 Windows 11 x64，Windows 10 版本 2004（Build 19041）及以上也可安装，但任务栏外观不可用。
- 在开始菜单添加“冰蓝桌面”；桌面快捷方式为可选项，默认不勾选。
- “登录 Windows 时启动冰蓝桌面”默认勾选，写入当前用户的单个启动项；可在设置中心“通用”页开关。卸载时只要启动项启动的正是本安装位置的程序就删除，不论它由安装程序还是应用创建；指向其他位置的同名启动项保留。
- 安装目录附带面向用户的 `使用说明.md`，开始菜单提供“冰蓝桌面使用说明”入口。
- 覆盖安装（更新）前先结束本安装位置运行中的冰蓝桌面，并运行 `--restore-taskbar` 恢复任务栏和桌面图标，再替换文件。恢复未完成时检查点保留，新版本启动时再次恢复。
- 安装完成后可直接启动冰蓝桌面；升级安装时先关闭正在运行的冰蓝桌面。应用内更新以 `/SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1` 启动安装程序，带 `/UPDATE=1` 时装完自动重新打开冰蓝桌面。
- 卸载时先结束本安装位置的冰蓝桌面进程（按完整路径匹配，不影响同名程序），再运行 `BingLan.exe --restore-taskbar` 撤销任务栏和清爽桌面改动，然后删除程序文件、快捷方式和启动项；恢复失败时提示用户在 Windows 设置和桌面右键菜单中手动恢复，并且不再询问删除个人数据，保留其中的恢复记录。
- 卸载最后询问是否删除个人数据 `%LOCALAPPDATA%\BingLanWidgets`（组件、待办、便签、文件映射、设置和备份），默认“否”；选择“是”时移到回收站。静默卸载（`/SILENT`、`/VERYSILENT`）始终保留数据。

## 依赖记录

| 项目 | 内容 |
| --- | --- |
| 依赖 | Inno Setup 6.7.3（构建工具，不随应用分发） |
| 用途 | 生成安装程序：文件安装、快捷方式、启动项、卸载和数据保留询问 |
| 许可证 | Inno Setup License：允许包括商业用途在内的任何用途；官方请求商业用户购买许可证，但不强制，个人和非商业使用免费 |
| 打包体积 | 安装程序约 53 MB；安装后约 159 MiB（256 个文件），主要为自包含 .NET 运行时 |
| 运行时成本 | 无，Inno Setup 只在构建时使用 |
| 可替代方案 | MSI（开发机上 Windows Installer 对按用户安装报错 2503，无法使用）；依赖框架的发布（安装包约 1 MB，但需先安装 .NET 10 桌面运行时）；MSIX（需要代码签名证书） |

### 内置字体

| 项目 | 内容 |
| --- | --- |
| 依赖 | 得意黑 Smiley Sans v2.0.1（`SmileySans-Oblique.ttf`）；Jost 3.5（Thin、Light、Book、Medium 四个静态字重）；Quicksand（Light、Regular、Bold 三个静态字重，取自 andrew-paglinawan/QuicksandFamily 的 `fonts/statics`）；Abril Fatface（`AbrilFatface-Regular.ttf`，取自 google/fonts 的 `ofl/abrilfatface`） |
| 用途 | 字体选择器里的内置字体：得意黑用于中文标题和问候语，Jost 的细字重用于大号时钟，Quicksand 用于圆润的英文和数字，Abril Fatface 是 Bodoni 风格的衬线标题字，配合斜体用于“TODAY”这类英文标题 |
| 许可证 | 四者均为 SIL Open Font License 1.1，允许随软件分发；不单独出售字体文件，不改名再分发。许可证原文随程序安装到 `字体许可` 文件夹，源文件位于 `src/BingLan.App/Assets/Fonts/LICENSE-*.txt` |
| 打包体积 | 约 3.4 MB（得意黑 2.6 MB，其余 0.8 MB），以资源形式编入 `BingLan.dll` |
| 运行时成本 | 只在卡片选用时由 WPF 从程序资源加载；不安装到 Windows，不影响其他软件 |
| 可替代方案 | 中文正文字体（如霞鹜文楷、思源黑体）单个字重 10–25 MB，体积过大未纳入；WPF 不支持可变字体的字重轴，因此使用静态字重文件 |

## 当前候选包

2026-10-03 生成 `installer\bin\BingLan-Setup-0.1.0.exe`，文件大小 50,953,032 字节（约 48.6 MiB）。构建日志确认自有 `BingLan.Taskbar.Xaml.dll` 已压入安装包；安装目录、发布目录与源项目 DLL 的 SHA-256 均为 `9115B282CF47CA9C69BBC736CCF5D04CC9D5AF760F5840D4CE895B674FF6D502`。

本机覆盖安装退出码为 0，无需重启；安装前后个人配置哈希一致。在正常 Windows 桌面上下文启动后，透明模式的共享状态确认 1 个背景生效、错误码为 0，截图确认任务栏透出背景。最终设置除任务栏模式外与安装前逐项一致。新原生组件的卸载场景尚未执行。真机透明与恢复结果见 [任务栏验证](TASKBAR-QA.md)。

## 验证状态

2026-09-28 在开发机（Windows 11 Build 26200）验证：

| 场景 | 结果 |
| --- | --- |
| 静默安装 | 通过：退出码 0，256 个文件，开始菜单快捷方式、启动项和“应用”列表卸载项均已创建 |
| 从安装位置启动 | 通过：正常运行，旧版（Schema 11）状态迁移到 14 并保留 `.bak` |
| 卸载程序文件、快捷方式、启动项 | 通过：全部删除，无残留 |
| 卸载时选择删除个人数据 | 通过：数据目录移到回收站，可还原 |
| 卸载时选择保留个人数据 | 由默认按钮“否”保证；静默卸载保留数据 |
| 应用运行中覆盖安装 | 通过：退出码 0，旧进程被结束后替换文件（未勾选启动项、静默模式） |
| 应用自行创建启动项后卸载 | 通过：进程结束，启动项、程序目录删除，个人数据保留 |
| 启动项被改为指向其他位置后卸载 | 通过：该启动项保留，程序目录删除 |

未覆盖：跨版本升级（旧版本号安装包）、带界面的首次安装向导、其他 Windows 11 设备。测试期间使用了临时替身数据目录，本机真实数据前后一致。
