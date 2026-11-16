# 第三方内容

冰蓝桌面的源码由 keros68 编写，按 [PolyForm Noncommercial 1.0.0](LICENSE) 授权。以下第三方内容随程序或仓库分发，各自适用原许可证，不受上述许可证约束。

## 内置字体

字体文件和许可证原文位于 `src/BingLan.App/Assets/Fonts/`，安装后许可证复制到程序目录的 `字体许可` 文件夹。字体只从程序资源加载，不安装到 Windows。

| 字体 | 来源 | 版权 | 许可证 |
| --- | --- | --- | --- |
| 得意黑 Smiley Sans（`SmileySans-Oblique.ttf`） | [atelier-anchor/smiley-sans](https://github.com/atelier-anchor/smiley-sans)（https://atelier-anchor.com） | Copyright (c) 2022–2024 atelierAnchor | SIL Open Font License 1.1（`LICENSE-SmileySans.txt`） |
| Jost（`Jost-200-Thin.ttf`、`Jost-300-Light.ttf`、`Jost-400-Book.ttf`、`Jost-500-Medium.ttf`） | [indestructible-type/Jost](https://github.com/indestructible-type/Jost) | Copyright 2020 The Jost Project Authors | SIL Open Font License 1.1（`LICENSE-Jost.txt`） |
| Quicksand（`Quicksand-Light.ttf`、`Quicksand-Regular.ttf`、`Quicksand-Bold.ttf`） | [andrew-paglinawan/QuicksandFamily](https://github.com/andrew-paglinawan/QuicksandFamily) | Copyright 2011 The Quicksand Project Authors | SIL Open Font License 1.1（`LICENSE-Quicksand.txt`） |
| Abril Fatface（`AbrilFatface-Regular.ttf`） | [TypeTogether](https://www.type-together.com)（[google/fonts](https://github.com/google/fonts/tree/main/ofl/abrilfatface)） | Copyright (c) 2011 TypeTogether | SIL Open Font License 1.1（`LICENSE-AbrilFatface.txt`） |

## 内置城市数据

`src/BingLan.Core/Assets/city-library.cn.json` 内置国内省、市、区县名称与坐标，用于天气城市离线搜索，作为程序资源加载。来源、生成脚本与已知边界见 [docs/CITY-DATA.md](docs/CITY-DATA.md)。

| 内容 | 来源 | 许可证 |
| --- | --- | --- |
| 省市区县名称与层级 | [mumuy/data_location](https://github.com/mumuy/data_location) | MIT |
| 地名经纬度 | [pyecharts](https://github.com/pyecharts/pyecharts) `city_coordinates.json` | MIT |

## 构建工具

| 名称 | 用途 | 许可证 |
| --- | --- | --- |
| Microsoft.Windows.CsWin32 | `design-lab/BingLan.DesktopVisibilitySpike` 研究项目生成 Windows API 声明，只在编译时使用，不随程序发布 | MIT |

## 文档展示图背景

`docs/images/brand-header.png` 与 `docs/images/materials.png` 中的冰蓝渐层背景均由仓库脚本通过矢量渐变与几何图形自行绘制，不含第三方图片资产。

## 参考过的项目

产品设计参考过 Rainmeter、Pogget、Nexus Dock 和 TranslucentTB 等工具的行为，仓库中没有复制它们的代码或素材。“从 Rainmeter 皮肤导入外观”只在用户本机读取所选皮肤的文字设置，不随程序分发任何皮肤内容。
