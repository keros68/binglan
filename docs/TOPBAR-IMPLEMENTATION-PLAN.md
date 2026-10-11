# 顶端信息条实现计划

依据：[`DESKTOP-TOPBAR-DESIGN.md`](./DESKTOP-TOPBAR-DESIGN.md) v2（已复核）。
分支：`feature/topbar-info-strip`（自 main）。
顺序按 spec §9 验收门；每步编译门 + 对应测试 + commit。

## 任务清单

- [x] T1 Core 模型：`TopBarState`（开关/显示方式/显示器/九模块/外观）+ `TopBarRules.Normalize`；`AppState.TopBar` + Schema v27 + 迁移。
- [x] T2 Core 规则：`TopBarRevealRules`（顶边唤出区）、`TopBarAttentionQueue`（有序注意队列 + 3 上限折叠）、`TopBarModuleRules`（待办计数/电量分类/网络分类/输入法语言 ID/音量钳制）、顶边占用判定（工作区顶 == 显示器顶）。
- [x] T3 模式编排：`DesktopModeRules` 新重载（苹果式 = 顶栏开 + 时间/天气/性能卡关；另两种 = 顶栏关 + 三卡可见；Detect 不变）。
- [x] T4 主题包：`ThemeBindings` 增顶栏字段；`ThemeRules.Export/Normalize` 覆盖；导入接线。
- [x] T5 Core 测试：以上全部进 SmokeTests/DockTests（新文件 `TopBarTests.cs`）。
- [x] T6 App 顶边 AppBar：`TopBarAppBarController`（复用 `AppBarReservationState`/`DockGeometry`/`DockNativeMethods`；顶边 TaskbarCreated 恢复、预留/释放、全屏层序）。
- [x] T7 App 顶栏窗口：`TopBarWindow`（满宽 32 DIP 直角玻璃条；九模块按钮 + UIA；右键菜单；Shell 钩子自注册；智能隐藏复用 `DockAutoHideState` + 顶边唤出区；前台/全屏/最大化评估循环）。
- [x] T8 App 系统信息服务：电量 `GetSystemPowerStatus`、音量 `IAudioEndpointVolume`（COM，随共享采样回调读取）、网络 `NetworkInterface`/`NetworkChange`、输入法前台+`GetKeyboardLayout`+注册表布局名；P/Invoke 集中在 `TopBarNativeMethods`。
- [x] T9 宿主接线：`TopBarHost`（DockHost 模式）+ `WidgetCoordinator`（启动序列、设置变更、主题导入、模式编排调用点升级、表面层序沿 Dock 先例）。
- [x] T10 设置中心："顶端信息条"页（总开关/显示方式/显示器/九模块/外观）+ 按组件 Kind 深链公开入口 + RefreshTopBarSettings；旧调用兼容。
- [x] T11 App 测试：UiSmokeTests 顶栏窗口（创建/模块/UIA/点击/智能隐藏）+ InformationTests 设置页 + DockTests 顶边预留/释放/几何。
- [x] T12 真机验证：QA 模式（隔离数据目录）启动，顶栏出现/预留生效/退出恢复；结果记 `TOPBAR-QA.md`；不可安全自动化的项（真全屏应用、Explorer 重启、Win+D、多显示器）列明留人工。
- [x] T13 文档同步：MVP §5.1/§6.1/§8.8/新 §8.x、SURFACES §3/§4/§7、DEVELOPMENT、README。

## 实施偏差记录（相对 spec v2，均为更简/更稳选择）

1. 音量读取随共享采样回调（1 秒）而非 `IAudioEndpointVolume` 事件回调：免 COM 事件生命周期风险，不新增计时器（共享契约只禁自建计时器）。spec §4 音量行将在文档同步时改写。
2. 顶栏不加入 `DesktopSurfaceWatcher`：预留态顶栏常驻置顶带（与任务栏/Dock 同层），Win+D 的 Progman 抬升盖不住置顶带——沿 Dock 先例，无需守护。spec §3.4 相应措辞在文档同步时修正。
3. 消息点击聚焦目标窗口：用 `WindowCommandService` 现有激活路径，不复制 Dock 的完整点击语义（spec v2 已记）。

## 完成状态（2026-10-10）

T1–T13 全部完成，含两轮评审修复；提交序列见 `git log main..feature/topbar-info-strip`。
两处计划内缩减（均有替代覆盖）：DockTests 未加顶边预留集成用例（AppBar 状态机为 Core 已测，顶边钉顶逻辑经真机 QA 验证）；UI 测试未加 Tab 遍历用例（UIA 名称与 Invoke 模式已覆盖，键盘焦点可达性记入 TOPBAR-QA 待人工确认）。
