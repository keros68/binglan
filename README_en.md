<div align="center">

<img src="src/BingLan.App/Assets/Brand/binglan-icon.png" width="112" alt="BingLan icon">

# BingLan Desktop

[简体中文](README.md) · [Download](https://github.com/keros68/binglan/releases/latest) · [Get started](#get-started) · [User guide (Chinese)](docs/USER-GUIDE.md) · [Development (Chinese)](docs/DEVELOPMENT.md) · [License](#license)

**Manage desktop info cards, to-dos, file groups, a dock, and the taskbar on Windows 11 from one app.**

</div>

BingLan Desktop (冰蓝桌面) is a Windows 11 desktop companion built with .NET 10 and WPF. It replaces separate tools for desktop info widgets, file organizing, a dock, and taskbar styling. All modules share one process, one tray icon, and one settings window.

<p align="center">
  <img src="docs/images/desktop.jpg" alt="BingLan desktop: quick launch, folded file groups, centred clock, performance and weather, to-dos, greeting, and the dock">
</p>

## Features

- **Desktop info**: time and date, greeting, weather, and CPU / RAM / network are separate cards, each with its own font, size, colour, and position. A thin line that fades at both ends can be shown under the time.
- **To-dos and notes**: a to-do card starts with three empty items; ticking, adding, and deleting are separate actions. Notes hold plain multi-line text.
- **File groups**: map desktop files, folders, and apps into boxes, sort them by type automatically (which also clears entries whose original file was deleted), fold a box down to its title bar, and show items as large icons or a compact list. Turn on "file-box automation" on the Layout settings page (off by default) and new files landing on the desktop join their existing box on their own, while entries whose original file was deleted disappear from the box. Entries on unplugged drives or offline shares stay, marked as invalid, and can be relinked. Boxes record paths only; original files stay where they are.
- **Multiple monitors**: cards remember a position for each monitor setup. Unplug a laptop's external monitor and plug it back in, and the cards return to where they were on two screens.
- **Quick launch**: a row of line icons for This PC, Desktop, Documents, Downloads, Pictures, and the Recycle Bin by default. Icons, names, and targets are customizable; a target can be a folder, program, or file.
- **Dock**: pinned and running apps in separate sections, with a badge on apps that need attention. A button at the left opens the native Windows Start menu; it is shown by default and can be turned off in Dock settings. Right-click it to open the native Windows Quick Link menu. Drag a program or shortcut onto the dock to pin it; colour, opacity, and icon size are adjustable. Floating bars and small tools you don't want on the dock can be hidden from its right-click menu.
- **Taskbar**: default, transparent, blurred, or auto-hide. Move the pointer to the bottom edge to reveal the native taskbar; pointer activation can optionally be restricted to the two bottom corners. Windows may still show the taskbar when opening native system panels. Hidden tray icons remain accessible through the native taskbar; the preview also provides a top-bar entry. The original state comes back on exit or after a crash.
- **Top bar**: shows time, to-dos, weather, performance, and system status. Battery, network, and volume buttons open Windows Quick Settings when available, otherwise their respective Settings pages. The input-method button opens the system picker when available, otherwise Language settings; it does not switch input modes automatically.
- **Search**: a centered top-bar field, a movable and resizable separate field, or a top-bar icon. Searches desktop entries, Start-menu apps, and files already indexed by Windows by default. Optional Everything integration falls back to Windows Search when unavailable. If neither index is available, desktop and app search remains usable. BingLan does not change indexing scope, maintain an additional index, or search file contents.
- **Resource use**: measured on one 1440p Windows 11 PC, BingLan uses about 56–58 MB of memory; Rainmeter, Pogget, Nexus Dock, and TranslucentTB, the tools it replaces, used about 77.5 MB together (Task Manager "Memory" column, single measurement; varies with the machine and number of cards).
- **Look**: light glass, dark glass, or no backing; custom text and accent colours; bundled open-licence fonts. Fonts and colours can be imported, read-only, from a Rainmeter skin. Themes can be exported and shared without names, cities, to-dos, or file paths.
- **Settings**: six pages (Appearance, Desktop components, Dock, Top bar, Taskbar, General), with each setting in exactly one place. On Windows 11 the window uses the Mica material, falling back to a solid colour when transparency effects are off or high contrast is on.

<p align="center">
  <img src="docs/images/settings.png" width="760" alt="BingLan settings: desktop modes, clean desktop and card appearance on the Appearance page">
</p>

The interface is in Simplified Chinese.

## Get started

Stable version: [v0.2.31](https://github.com/keros68/binglan/releases/tag/v0.2.31) · [Installer](https://github.com/keros68/binglan/releases/download/v0.2.31/BingLan-Setup-0.2.31.exe) · [Portable](https://github.com/keros68/binglan/releases/download/v0.2.31/BingLan-0.2.31-portable.zip). Fixed some pinned apps failing to launch from the Dock: pins now keep the shortcut's arguments and working directory and use them on launch (for example AutoCAD and game launchers); apps pinned before upgrading need to be pinned again once to pick them up. A Tray button on the right of the top bar opens the native Windows hidden-icons panel and is on by default; it can be disabled in Top bar settings. A standalone [compatibility tool](https://github.com/keros68/binglan/releases/download/v0.2.31/BingLan-0.2.31-tray-preview.zip) is also available; see the [guide (Chinese)](docs/TRAY-PREVIEW.md).

Prerelease: [v0.2.32](https://github.com/keros68/binglan/releases/tag/v0.2.32) · [Installer](https://github.com/keros68/binglan/releases/download/v0.2.32/BingLan-Setup-0.2.32.exe) · [Portable](https://github.com/keros68/binglan/releases/download/v0.2.32/BingLan-0.2.32-portable.zip). Fixed blurry card icons in file boxes, the app picker and top-bar search: icons now come from the system image list at their highest-resolution frame (up to 256×256) and are scaled down with high-quality filtering. Fixed desktop icons not coming back after turning off Clean Desktop: turning it off now restores the icons even when the restore record was lost, and enabling it while the icons are already hidden asks you to show them first; the file box settings gained a "files on the desktop" hint with a one-click jump to the Clean Desktop switch. A standalone [compatibility tool](https://github.com/keros68/binglan/releases/download/v0.2.32/BingLan-0.2.32-tray-preview.zip) is also available; see the [guide (Chinese)](docs/TRAY-PREVIEW.md).

1. Open [Releases](https://github.com/keros68/binglan/releases/latest) and download `BingLan-Setup-*.exe` (installer) or `BingLan-*-portable.zip` (portable).
2. Installer: run it — it installs for the current user and needs no administrator rights. Portable: unzip anywhere and run `BingLan.exe` inside. Both include the .NET runtime and are not code-signed yet, so Windows asks for confirmation on first run.
3. Portable: to update, download the new zip and overwrite — don't use the in-app update (it installs the setup version); don't run the portable and installed copies at the same time.
4. On first launch, pick a layout and your common apps. Settings open from the tray icon or any card's right-click menu.

Requires Windows 11 x64. Windows 10 version 2004 or later also works, without the taskbar styles (transparent, blur, auto-hide) and without the Mica material in the settings window. See the [user guide (Chinese)](docs/USER-GUIDE.md) for details.

## Privacy

- No telemetry. To-dos, notes, file paths, app lists, and window titles stay on this computer under `%LOCALAPPDATA%\BingLanWidgets`.
- The weather city is chosen by searching by hand. Chinese provinces, cities, and districts are found in a bundled offline list without any network access; other places fall back to OpenStreetMap Nominatim, and requests send only the search text and the chosen city's coordinates. IP address and device location are never used.
- Once a day the app asks GitHub for the latest version number (can be turned off under Settings → 通用). A new version is only announced; you decide whether to download and install it.
- Adding, sorting, or removing items in a file group changes only the mapping. Original files are never moved, renamed, or deleted.

## Build from source

Requires the .NET 10 SDK and PowerShell 7. Building the installer also needs Inno Setup 6 and the Visual Studio 2022 C++ build tools.

```powershell
dotnet build .\BingLan.slnx -c Release
dotnet run --project .\tests\BingLan.SmokeTests\BingLan.SmokeTests.csproj -c Release
pwsh -NoProfile -File .\installer\build.ps1 -Version 0.2.32
```

Full test commands, the technical design, and the current implementation are in the [development notes (Chinese)](docs/DEVELOPMENT.md); installer details are in [docs/INSTALLER.md](docs/INSTALLER.md).

## License

[PolyForm Noncommercial 1.0.0](LICENSE), Copyright © 2026 keros68. Free for personal learning, research, and other noncommercial use; commercial use requires a separate licence. Bundled fonts and other third-party content are listed in [NOTICE](NOTICE.md).
