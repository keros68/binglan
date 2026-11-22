# 第三方组件说明

## Windows SDK 原生头文件与系统接口

- 组件：Windows 10/11 SDK C++/WinRT 头文件（构建时编译为自有 `BingLan.Composition.dll` 与 `BingLan.Taskbar.Xaml.dll`）。
- 用途：访问系统自带的 Windows Composition 为 Dock 与隐藏把手绘制实时背景模糊和圆角，以及在经验证版本上适配任务栏背景。
- 随程序分发：仅包含项目自有原生组件 `BingLan.Composition.dll` 与 `BingLan.Taskbar.Xaml.dll`（静态链接 `/MT`，不分发 `Microsoft.Windows.SDK.NET.dll`、`WinRT.Runtime.dll`、WinUI 或 Win2D）。
- 许可：Windows SDK 许可，https://aka.ms/WinSDKLicenseURL 。

Windows Composition 由操作系统自身提供；应用不读取或上传背景像素。

内置字体的许可证原文位于程序目录的“字体许可”文件夹。

## 内置开源字体

- **Google Sans**（v76，Regular / Medium / Bold 静态字重，约 6.0 MB）：用于应用界面拉丁字母、数字与常见符号（中文由系统 `Microsoft YaHei UI` 兜底）；来源 Google Fonts（`https://fonts.google.com/specimen/Google+Sans`），许可 SIL Open Font License 1.1（`LICENSE-GoogleSans.txt`）。
- **得意黑 / Jost / Quicksand / Abril Fatface**：用于桌面卡片可选标题与时钟字体，均采用 SIL Open Font License 1.1。
