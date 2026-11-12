# 第三方组件说明

## Windows SDK 原生头文件与系统接口

- 组件：Windows 10/11 SDK C++/WinRT 头文件（构建时编译为自有 `BingLan.Composition.dll` 与 `BingLan.Taskbar.Xaml.dll`）。
- 用途：访问系统自带的 Windows Composition 为 Dock 与隐藏把手绘制实时背景模糊和圆角，以及在经验证版本上适配任务栏背景。
- 随程序分发：仅包含项目自有原生组件 `BingLan.Composition.dll` 与 `BingLan.Taskbar.Xaml.dll`（静态链接 `/MT`，不分发 `Microsoft.Windows.SDK.NET.dll`、`WinRT.Runtime.dll`、WinUI 或 Win2D）。
- 许可：Windows SDK 许可，https://aka.ms/WinSDKLicenseURL 。

Windows Composition 由操作系统自身提供；应用不读取或上传背景像素。

内置字体的许可证原文位于程序目录的“字体许可”文件夹。
