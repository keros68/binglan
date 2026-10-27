using BingLan.Core.Models;
using BingLan.Core.Services;
using BingLan.Core.Taskbar;
using BingLan.Core.Themes;
using BingLan.Core.TopBar;
using BingLan.SmokeTests;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

var failures = new List<string>();
Run("待办编辑、勾选、排序", TestTodo);
Run("空白待办在结束输入后删除", TestRemoveBlank);
Run("待办日期字段、完成时间与周历规则", TestTodoDates);
Run("按日同步待办行", TestSyncDayItems);
Run("跨日移动待办", TestMoveToDate);
Run("待办日期持久化与旧状态迁移", TestTodoDatePersistence);
Run("周历农历标注", TestLunarDayText);
Run("顶栏只统计今日待办", TestTopBarTodayCount);
Run("文件映射去重与移除", TestMappings);
Run("桌面项目导入去重、排序与持久化", TestDesktopGrouping);
Run("桌面软件、文件夹和文件安全分类", TestDesktopClassification);
Run("文件盒系统入口、失效判定与重新定位", FileBoxRuleTests.Run);
Run("位置、尺寸和内容重启保留", TestPersistence);
Run("卡片按显示器排列记住位置", DisplayLayoutTests.TestPositionPerArrangement);
Run("按排列记住的位置保存与 v21 迁移", DisplayLayoutTests.TestPersistenceAndMigration);
Run("圆角规则与 v1-v2 状态迁移", TestCornerRadiusMigration);
Run("材质、文字颜色与标题字体规则及 v3-v9 状态迁移", TestAppearanceMigration);
Run("普通便签持久化与 v6-v9 状态迁移", TestNotePersistenceAndMigration);
Run("桌面信息持久化与 v7-v9 状态迁移", TestInformationPersistenceAndMigration);
Run("三套布局与内置组件状态持久化及 v9-v12 迁移", TestDesktopExperiencePersistence);
Run("任务栏兼容性按模式判定", TestTaskbarCompatibility);
Run("任务栏检查点与恢复计划", TestTaskbarRecovery);
Run("任务栏连续失败熔断", TestTaskbarBreaker);
Run("任务栏设置持久化与 v13-v14 迁移", TestTaskbarPersistence);
Run("三种桌面模式编排 Dock 与任务栏", TestDesktopModes);
Run("Dock 与悬浮图标形变动效规格", DockMorphTests.Run);
Run("Dock 智能隐藏的延迟与最大化立即收起", DockSmartHideTests.Run);
Run("顶端信息条模型、持久化与编排", TopBarTests.Run);
Run("顶栏搜索匹配、热键与 v29 迁移", TopBarSearchTests.Run);
Run("备份列表与重启后恢复", TestBackupRestore);
Run("首次引导只对新安装显示", TestOnboardingFlagMigration);
Run("主题导出不含个人信息", TestThemeExportExcludesPersonalInfo);
Run("主题包往返与相对布局", TestThemePackageRoundTripAndRelativeLayout);
Run("主题包拒绝不安全内容", TestThemePackageRejectsUnsafeContent);
Run("主题值规范化", TestThemeValueNormalization);
Run("主题应用槽位匹配", TestThemeSlotBindingResolution);
Run("主题预览图往返读取", ThemePreviewTests.TestValidPreviewRoundTrips);
Run("主题包无预览图时正常读取", ThemePreviewTests.TestMissingPreviewIsFine);
Run("非 PNG 预览图被忽略", ThemePreviewTests.TestNonPngPreviewIsIgnored);
Run("超大预览图被拒绝", ThemePreviewTests.TestOversizedPreviewIsRejected);
Run("带预览图的导出仍不含个人信息", ThemePreviewTests.TestExportWithPreviewStillHasNoPersonalData);
Run("清爽桌面只改图标位", CleanDesktopTests.TestOnlyTheIconBitChanges);
Run("清爽桌面隐藏与恢复计划", CleanDesktopTests.TestHideAndRestorePlans);
Run("显示桌面判定与提升回落规则", DesktopSurfaceTests.TestShownStateAndActions);
Run("显示桌面判定中外壳窗口的归类", DesktopSurfaceTests.TestOrdinaryWindowClassification);
Run("更新版本号与每日检查节奏", UpdateServiceTests.TestVersionAndSchedule);
Run("更新发布信息解析与安全过滤", UpdateServiceTests.TestReleaseParsing);
Run("检查更新结果与请求内容（离线桩）", UpdateServiceTests.TestCheck);
Run("更新安装包大小与 SHA-256 校验", UpdateServiceTests.TestVerifiedDownload);
Run("更新设置 v20 迁移与保存", UpdateServiceTests.TestStateMigration);
Run("清爽桌面识别用户接管", CleanDesktopTests.TestTakeoverDetection);
Run("清爽桌面无记录时关闭仍恢复显示", CleanDesktopTests.TestTurnOffWithoutRecordPlan);
Run("清爽桌面检查点往返与拒绝", CleanDesktopTests.TestCheckpointRoundTripAndRejection);
Run("清爽桌面设置迁移", CleanDesktopTests.TestSettingMigration);
Run("空状态文件从备份恢复且不覆盖备份", CleanDesktopTests.TestEmptyStateFallsBackToBackup);
Run("整体风格迁移与取值范围", ThemeStyleTests.TestStyleMigrationAndLimits);
Run("卡片外观配色与居中时钟布局", ThemeStyleTests.TestGlassAndCenterClockPreset);
Run("四种竖屏预设边界、最小尺寸与横屏坐标", ThemeStyleTests.TestPortraitPresets);
Run("任务栏只在两角唤出时底边细条的位置", ThemeStyleTests.TestTaskbarCornerStrip);
Run("主题包整体风格与自定义图标往返", ThemeStyleTests.TestStyleAndIconsRoundTrip);
Run("主题包丢弃不安全图标", ThemeStyleTests.TestUnsafeIconsAreDropped);
Run("主题包清单校验", ThemeStyleTests.TestManifestIsValidated);
Run("备份列表摘要", BackupSummaryTests.TestBackupIsDescribed);
Run("备份可以删除且目录外被拒绝", BackupSummaryTests.TestBackupCanBeDeleted);
Run("新建卡片标题不重名", BackupSummaryTests.TestNewCardTitlesAreUnique);
Run("无法读取的状态文件以默认状态启动并留存", StateRecoveryTests.TestUnreadableStateStartsFromDefaults);
Run("像素尺寸过大的 PNG 被拒绝", StateRecoveryTests.TestHugePngIsRejected);
Run("读取 Rainmeter 皮肤的字体、颜色和底板", RainmeterLookTests.TestPerformanceSkinLook);
Run("快捷入口列表规则", StateRecoveryTests.TestQuickPlaceRules);
Run("Rainmeter 皮肤变量与空皮肤", RainmeterLookTests.TestVariablesAndEmptySkins);

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures));
    return 1;
}

Console.WriteLine("全部核心烟雾测试通过。");
return 0;

void Run(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"通过：{name}");
    }
    catch (Exception ex)
    {
        failures.Add($"失败：{name} - {ex.Message}");
    }
}

static void TestTodo()
{
    var defaultWidget = TodoService.CreateDefaultWidget();
    Assert(defaultWidget.Items.Count == 0, "新建待办卡片应为空白（不自带占位行）");

    var widget = new TodoWidgetState();
    var first = TodoService.Add(widget, "第一项");
    var second = TodoService.Add(widget, "第二项");
    first.Text = "已编辑";
    first.IsCompleted = true;
    Assert(TodoService.Move(widget, second.Id, -1), "第二项应可上移");
    Assert(widget.Items[0].Id == second.Id, "排序结果错误");
    Assert(widget.Items[1].Text == "已编辑" && widget.Items[1].IsCompleted, "编辑或完成状态错误");
    Assert(TodoService.RemoveCompleted(widget) == 1, "应只清理一条已完成待办");
    Assert(widget.Items.Count == 1 && widget.Items[0].Id == second.Id && widget.Items[0].Order == 0,
        "清理已完成后应保留未完成待办并重新排序");
    Assert(TodoService.RemoveCompleted(widget) == 0, "没有已完成待办时不应删除");
}

static void TestTodoDates()
{
    var widget = new TodoWidgetState();
    var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
    var tuesday = monday.AddDays(1);

    var undated = TodoService.Add(widget, "无日期");
    Assert(undated.CreatedDate is not null, "新建待办应记录创建时间");
    Assert(undated.Date is null && undated.CompletedDate is null, "默认待办无日期无完成时间");

    var dated = TodoService.Add(widget, "周二任务", tuesday);
    Assert(dated.Date == tuesday, "周历新增待办应落到指定日期");
    Assert(TodoService.ItemsForDate(widget, tuesday).Single().Id == dated.Id, "按日期取待办结果错误");
    Assert(!TodoService.ItemsForDate(widget, monday).Any(), "未放置的日期不应有待办");

    Assert(TodoService.SetCompleted(widget, dated.Id, true), "应能勾选待办");
    Assert(dated.IsCompleted && dated.CompletedDate is not null, "勾选后应记录完成时间");
    var stampedAt = dated.CompletedDate!.Value;
    Assert(TodoService.SetCompleted(widget, dated.Id, true) == false, "重复勾选同状态应返回 false 且不盖戳");
    Assert(dated.CompletedDate == stampedAt, "重复勾选不应改写完成时间");
    Assert(TodoService.SetCompleted(widget, dated.Id, false), "应能取消勾选");
    Assert(!dated.IsCompleted && dated.CompletedDate is null, "取消勾选应清空完成时间");
    Assert(TodoService.SetCompleted(widget, dated.Id, true), "应能重新勾选");
    Assert(dated.CompletedDate is not null, "重新勾选应重新记录完成时间");

    // Container regeneration replays Checked events; stamping must be idempotent
    // so render passes never rewrite completion times.
    var idempotent = TodoService.Add(widget, "幂等盖戳", monday);
    idempotent.IsCompleted = true;
    TodoService.StampCompletion(idempotent);
    var firstStamp = idempotent.CompletedDate;
    Thread.Sleep(2);
    TodoService.StampCompletion(idempotent);
    Assert(idempotent.CompletedDate == firstStamp, "重复盖戳应保持原完成时间不变");

    // Stamping follows the flag, covering the paths that toggle IsCompleted directly.
    var stamped = TodoService.Add(widget, "顶栏勾选", monday);
    stamped.IsCompleted = true;
    TodoService.StampCompletion(stamped);
    Assert(stamped.CompletedDate is not null, "直接勾选后也应记录完成时间");
    stamped.IsCompleted = false;
    TodoService.StampCompletion(stamped);
    Assert(stamped.CompletedDate is null, "直接取消后应清空完成时间");

    // Moving inside one day only swaps neighbours of that day.
    var anotherTuesday = TodoService.Add(widget, "周二第二条", tuesday);
    Assert(TodoService.MoveWithinDate(widget, anotherTuesday.Id, -1), "同日内应可上移");
    var tuesdayItems = TodoService.ItemsForDate(widget, tuesday).ToList();
    Assert(
        tuesdayItems[0].Id == anotherTuesday.Id && tuesdayItems[1].Id == dated.Id,
        "同日内上移应只交换同日条目");
    Assert(
        widget.Items.OrderBy(x => x.Order).Select(x => x.Order)
            .SequenceEqual(Enumerable.Range(0, widget.Items.Count)),
        "移动后全局顺序应重新编号");
    Assert(!TodoService.MoveWithinDate(widget, undated.Id, -1), "无日期待办不应参与同日移动");

    var todayEntry = TodoService.Add(widget, "今日格子", DateTime.Today);
    Assert(TodoService.BelongsToTodayList(undated, DateTime.Today), "无日期待办属于今日列表");
    Assert(TodoService.BelongsToTodayList(todayEntry, DateTime.Today), "今日格子待办属于今日列表");
    Assert(!TodoService.BelongsToTodayList(dated, DateTime.Today), "其他日期待办不属于今日列表");

    // Clearing completed entries only reaches the today scope, never dated history.
    todayEntry.IsCompleted = true;
    undated.IsCompleted = true;
    Assert(
        TodoService.RemoveCompleted(widget, x => TodoService.BelongsToTodayList(x, DateTime.Today)) >= 2,
        "清理今日应删除今日范围内的已完成待办");
    Assert(widget.Items.Any(x => x.Id == dated.Id), "其他日期的已完成历史不应被清理");
    Assert(!widget.Items.Any(x => x.Id == todayEntry.Id), "今日已完成待办应被清理");
    Assert(!widget.Items.Any(x => x.Id == undated.Id), "今日范围内已完成待办应被清理");

    // 完成自动下滑：勾选完成时沉入当前范围末尾（已完成区尾部），取消完成时回到未完成区末尾（含无日期与今日日期混合场景）。
    var sinkWidget = new TodoWidgetState();
    var s1 = TodoService.Add(sinkWidget, "任务1");
    var s2 = TodoService.Add(sinkWidget, "任务2（今日日期）", DateTime.Today);
    var s3 = TodoService.Add(sinkWidget, "任务3");
    var otherDayItem = TodoService.Add(sinkWidget, "周二保留项", tuesday);
    s1.IsCompleted = true;
    Assert(TodoService.SinkCompleted(sinkWidget, s1.Id, DateTime.Today), "勾选首条后应自动下滑");
    var todayOrdered = sinkWidget.Items
        .Where(x => TodoService.BelongsToTodayList(x, DateTime.Today))
        .OrderBy(x => x.Order)
        .Select(x => x.Id)
        .ToList();
    Assert(todayOrdered.SequenceEqual([s2.Id, s3.Id, s1.Id]), "首条完成后应排在今日范围最后");
    s2.IsCompleted = true;
    Assert(TodoService.SinkCompleted(sinkWidget, s2.Id, DateTime.Today), "勾选第二条后应继续下滑至末尾");
    todayOrdered = sinkWidget.Items
        .Where(x => TodoService.BelongsToTodayList(x, DateTime.Today))
        .OrderBy(x => x.Order)
        .Select(x => x.Id)
        .ToList();
    Assert(todayOrdered.SequenceEqual([s3.Id, s1.Id, s2.Id]), "新完成的条目应排在已完成区末尾");
    s2.IsCompleted = false;
    Assert(TodoService.SinkCompleted(sinkWidget, s2.Id, DateTime.Today), "取消勾选末尾已完成项后应回升至未完成区末尾");
    todayOrdered = sinkWidget.Items
        .Where(x => TodoService.BelongsToTodayList(x, DateTime.Today))
        .OrderBy(x => x.Order)
        .Select(x => x.Id)
        .ToList();
    Assert(todayOrdered.SequenceEqual([s3.Id, s2.Id, s1.Id]), "取消完成的条目应回到已完成条目之前");
    Assert(sinkWidget.Items.Any(x => x.Id == otherDayItem.Id), "其他日期条目不应丢失");
}

static void TestSyncDayItems()
{
    var widget = new TodoWidgetState();
    var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
    var first = TodoService.Add(widget, "原始一", monday);
    first.IsCompleted = true;
    TodoService.StampCompletion(first);
    TodoService.Add(widget, "原始二", monday);
    TodoService.Add(widget, "别的一天", monday.AddDays(1));

    // 编辑框提交：带序号前缀与空行的文本，按行同步当天的条目。
    var kept = TodoService.SyncDayItems(widget, monday, new[] { "1. 更新一", "2. 更新二", "", "3. 新增三" });
    Assert(kept == 3, "同步后当天应保留三条");
    var dayItems = TodoService.ItemsForDate(widget, monday).ToList();
    Assert(
        dayItems[0].Text == "更新一" && dayItems[0].IsCompleted && dayItems[0].CompletedDate is not null,
        "已有序目应更新文本且保留完成状态");
    Assert(dayItems[1].Text == "更新二" && !dayItems[1].IsCompleted, "未完成序目不应被误标完成");
    Assert(
        dayItems[2].Text == "新增三" && dayItems[2].Date == monday && dayItems[2].CreatedDate is not null,
        "新增行应落到当天并记录创建时间");
    Assert(
        TodoService.ItemsForDate(widget, monday.AddDays(1)).Single().Text == "别的一天",
        "其他日期的条目不应被同步波及");

    // 行数变少：多余条目按末尾删除。
    TodoService.SyncDayItems(widget, monday, new[] { "1. 只留一条" });
    var reduced = TodoService.ItemsForDate(widget, monday).ToList();
    Assert(reduced.Count == 1 && reduced[0].Text == "只留一条", "行数变少应删除末尾多余条目");

    // 数字开头的正文不应被当成序号剥掉。
    TodoService.SyncDayItems(widget, monday, new[] { "3D建模实训", "42" });
    var numbered = TodoService.ItemsForDate(widget, monday).ToList();
    Assert(numbered.Count == 2 && numbered[0].Text == "3D建模实训" && numbered[1].Text == "42",
        "数字开头的正文不应被误剥序号");

    // 文本优先对齐：在首行已完成条目后、删除首行或首行前插入新行时，原条目的 Id 与完成状态跟随文本保留。
    TodoService.SyncDayItems(widget, monday, new[] { "任务A", "任务B" });
    var beforeEdit = TodoService.ItemsForDate(widget, monday).ToList();
    TodoService.SetCompleted(widget, beforeEdit[0].Id, true);
    var taskBId = beforeEdit[1].Id;
    TodoService.SyncDayItems(widget, monday, new[] { "任务B" });
    var afterDeleteFirst = TodoService.ItemsForDate(widget, monday).Single();
    Assert(
        afterDeleteFirst.Id == taskBId && !afterDeleteFirst.IsCompleted && afterDeleteFirst.CompletedDate is null,
        "删除已完成首行后，保留的第二行不应继承首行的完成状态");

    TodoService.SetCompleted(widget, afterDeleteFirst.Id, true);
    var completedStamp = afterDeleteFirst.CompletedDate;
    TodoService.SyncDayItems(widget, monday, new[] { "前置新任务", "任务B" });
    var afterInsertTop = TodoService.ItemsForDate(widget, monday).ToList();
    Assert(
        afterInsertTop.Count == 2
            && afterInsertTop[0].Text == "前置新任务" && !afterInsertTop[0].IsCompleted
            && afterInsertTop[1].Id == taskBId && afterInsertTop[1].IsCompleted && afterInsertTop[1].CompletedDate == completedStamp,
        "在首行前插入新任务后，原已完成条目应保持自身 Id 与完成时间戳");

    // 原位修改已完成首行文字并保留第二行时，首行位于同一锚点区间，应保留原 Id、完成状态与完成时间戳。
    TodoService.SyncDayItems(widget, monday, new[] { "任务A", "任务B" });
    var pairBefore = TodoService.ItemsForDate(widget, monday).ToList();
    TodoService.SetCompleted(widget, pairBefore[0].Id, true);
    TodoService.SetCompleted(widget, pairBefore[1].Id, false);
    var completedAId = pairBefore[0].Id;
    var completedAStamp = pairBefore[0].CompletedDate;
    var uncompletedBId = pairBefore[1].Id;
    TodoService.SyncDayItems(widget, monday, new[] { "任务A（已改名）", "任务B" });
    var pairInPlaceEdit = TodoService.ItemsForDate(widget, monday).ToList();
    Assert(
        pairInPlaceEdit.Count == 2
            && pairInPlaceEdit[0].Text == "任务A（已改名）"
            && pairInPlaceEdit[0].Id == completedAId
            && pairInPlaceEdit[0].IsCompleted
            && pairInPlaceEdit[0].CompletedDate == completedAStamp
            && pairInPlaceEdit[1].Text == "任务B"
            && pairInPlaceEdit[1].Id == uncompletedBId
            && !pairInPlaceEdit[1].IsCompleted,
        "原位修改已完成条目的文字时，应保留其原 Id、完成状态与完成时间戳");

    // 删掉已完成首行（使任务B上移到首行）并在末尾追加新任务时，末尾新任务不应跨锚点复用已删首行的完成状态与 Id。
    TodoService.SyncDayItems(widget, monday, new[] { "任务B", "末尾新增任务C" });
    var pairAfterDeleteAndAppend = TodoService.ItemsForDate(widget, monday).ToList();
    Assert(
        pairAfterDeleteAndAppend.Count == 2
            && pairAfterDeleteAndAppend[0].Text == "任务B"
            && pairAfterDeleteAndAppend[0].Id == uncompletedBId
            && !pairAfterDeleteAndAppend[0].IsCompleted
            && pairAfterDeleteAndAppend[1].Text == "末尾新增任务C"
            && pairAfterDeleteAndAppend[1].Id != completedAId
            && !pairAfterDeleteAndAppend[1].IsCompleted
            && pairAfterDeleteAndAppend[1].CompletedDate is null,
        "删掉已完成首行并在保留行之后追加新任务时，新任务不应跨锚点复用已删完成项的完成状态");

    // 删掉一条已完成的任务并同时改写相邻的另一条：无法判断改写的是哪条，新文字不得继承已删条目的完成状态。
    var deleteAndEdit = new TodoWidgetState();
    var day = DateTime.Today;
    var removedDone = TodoService.Add(deleteAndEdit, "A", day);
    TodoService.SetCompleted(deleteAndEdit, removedDone.Id, true);
    TodoService.Add(deleteAndEdit, "B", day);
    var anchorC = TodoService.Add(deleteAndEdit, "C", day);
    TodoService.SyncDayItems(deleteAndEdit, day, new[] { "D", "C" });
    var afterDeleteAndEdit = TodoService.ItemsForDate(deleteAndEdit, day).ToList();
    Assert(
        afterDeleteAndEdit.Count == 2
            && afterDeleteAndEdit[0].Text == "D" && !afterDeleteAndEdit[0].IsCompleted
            && afterDeleteAndEdit[0].CompletedDate is null && afterDeleteAndEdit[0].Id != removedDone.Id
            && afterDeleteAndEdit[1].Id == anchorC.Id,
        "删掉已完成条目的同时改写另一条，改写的行不应继承已删条目的完成状态与 Id");

    // 改写一条已完成任务的同时新增一行：行数不少于条目数，没有删除的迹象，改写的行仍保留原 Id 与完成状态。
    var editAndAdd = new TodoWidgetState();
    var doneOriginal = TodoService.Add(editAndAdd, "原文", day);
    TodoService.SetCompleted(editAndAdd, doneOriginal.Id, true);
    TodoService.SyncDayItems(editAndAdd, day, new[] { "改后", "另加一条" });
    var afterEditAndAdd = TodoService.ItemsForDate(editAndAdd, day).ToList();
    Assert(
        afterEditAndAdd.Count == 2 && afterEditAndAdd[0].Id == doneOriginal.Id
            && afterEditAndAdd[0].IsCompleted && afterEditAndAdd[1].Id != doneOriginal.Id
            && !afterEditAndAdd[1].IsCompleted,
        "改写已完成任务并同时新增一行时，被改写的行应保留 Id 与完成状态");

    // 只改一条已完成任务的文字、没有删除：行数等于条目数，照旧原地保留。
    var onlyEdit = new TodoWidgetState();
    var doneOnly = TodoService.Add(onlyEdit, "旧文字", day);
    TodoService.SetCompleted(onlyEdit, doneOnly.Id, true);
    TodoService.SyncDayItems(onlyEdit, day, new[] { "新文字" });
    var afterOnlyEdit = TodoService.ItemsForDate(onlyEdit, day).Single();
    Assert(
        afterOnlyEdit.Id == doneOnly.Id && afterOnlyEdit.IsCompleted && afterOnlyEdit.Text == "新文字",
        "只改文字时应原地保留 Id 与完成状态");
}

static void TestMoveToDate()
{
    var widget = new TodoWidgetState();
    var monday = DateTime.Today.AddDays(-(((int)DateTime.Today.DayOfWeek + 6) % 7));
    var a = TodoService.Add(widget, "没做完的", monday);
    a.IsCompleted = false;
    TodoService.Add(widget, "别的一天", monday.AddDays(1));

    Assert(TodoService.MoveToDate(widget, a.Id, monday.AddDays(3)), "应能移动到大后天");
    Assert(a.Date == monday.AddDays(3) && a.Text == "没做完的" && !a.IsCompleted,
        "跨日移动应只改日期，文字与完成状态不变");
    Assert(!TodoService.ItemsForDate(widget, monday).Any(), "源日期不应残留条目");
    Assert(TodoService.ItemsForDate(widget, monday.AddDays(3)).Single().Id == a.Id, "目标日期应有该条目");

    var b = TodoService.Add(widget, "已完成项", monday);
    b.IsCompleted = true;
    TodoService.StampCompletion(b);
    Assert(TodoService.MoveToDate(widget, b.Id, monday.AddDays(3)), "应能移动已完成项");
    var moved = TodoService.ItemsForDate(widget, monday.AddDays(3)).Single(x => x.Text == "已完成项");
    Assert(moved.IsCompleted && moved.CompletedDate is not null, "跨日移动应保留完成状态与时间");

    Assert(!TodoService.MoveToDate(widget, b.Id, monday.AddDays(3)), "移到同一日期应返回 false 且无操作");
    Assert(!TodoService.MoveToDate(widget, Guid.NewGuid(), monday), "移动不存在的条目应返回 false");

    // withinDayIndex 参数应将条目精确插回目标日期的 0-based 日内位置，并保证多日穿插下全局 Order 唯一且连续。
    var m1 = TodoService.Add(widget, "周一首项", monday);
    var m2 = TodoService.Add(widget, "周一次项", monday);
    Assert(TodoService.MoveToDate(widget, b.Id, monday, withinDayIndex: 1), "应能带 withinDayIndex 移回周一中间位置");
    var mondayOrdered = TodoService.ItemsForDate(widget, monday).Select(x => x.Id).ToList();
    Assert(
        mondayOrdered.SequenceEqual(new[] { m1.Id, b.Id, m2.Id }),
        "带 withinDayIndex 的跨日移动应精确插入指定日内下标");
    Assert(
        widget.Items.Select(x => x.Order).OrderBy(x => x).SequenceEqual(Enumerable.Range(0, widget.Items.Count)),
        "带 withinDayIndex 的跨日移动后全局 Order 应唯一且连续");
}

static void TestTodoDatePersistence()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-todo-dates-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        var state = store.Load();
        var widget = state.TodoWidgets[0];
        widget.WeeklyView = true;
        var dated = TodoService.Add(widget, "带日期", new DateTime(2026, 10, 20));
        TodoService.SetCompleted(widget, dated.Id, true);
        store.Save(state);

        var restored = store.Load().TodoWidgets.Single();
        Assert(restored.WeeklyView, "周历视图开关未持久化");
        var restoredItem = restored.Items.Single(x => x.Text == "带日期");
        Assert(restoredItem.Date == new DateTime(2026, 10, 20), "待办日期未持久化");
        Assert(
            restoredItem.Date!.Value.Kind == DateTimeKind.Unspecified
                && restoredItem.Date.Value.TimeOfDay == TimeSpan.Zero,
            "待办日期应为无时区种类的午夜值");
        Assert(
            restoredItem.CreatedDate is not null && restoredItem.CompletedDate is not null,
            "创建与完成时间未持久化");
        Assert(store.Load().SchemaVersion > 33, "带日期的状态必须使用高于 v33 的版本，阻止旧版静默覆盖日期");

        Assert(restored.WeeklyWeekCount == 4, "周数默认值应为 4");
        widget.WeeklyWeekCount = 2;
        store.Save(state);
        Assert(store.Load().TodoWidgets.Single().WeeklyWeekCount == 2, "周数未持久化");
        var tampered = File.ReadAllText(store.StatePath).Replace("\"WeeklyWeekCount\": 2", "\"WeeklyWeekCount\": 99");
        File.WriteAllText(store.StatePath, tampered);
        Assert(store.Load().TodoWidgets.Single().WeeklyWeekCount == 4, "越界周数应被归一化回 4");
        widget.WeeklyWeekCount = 2;
        store.Save(state);

        // Undated v33 entries retain their identity and completion state on upgrade.
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 33,
              "TodoWidgets": [
                {
                  "Title": "官方版数据",
                  "HideCompleted": false,
                  "Items": [
                    { "Id": "11111111-2222-3333-4444-555555555555", "Text": "官方待办", "IsCompleted": true, "Order": 0 }
                  ]
                }
              ]
            }
            """);
        var fromOfficial = store.Load().TodoWidgets.Single();
        Assert(fromOfficial.Items.Single().Date is null, "官方数据读取后不应凭空出现日期");
        Assert(fromOfficial.Items.Single().Id == Guid.Parse("11111111-2222-3333-4444-555555555555")
            && fromOfficial.Items.Single().IsCompleted, "迁移应保留旧条目的标识与完成状态");
        Assert(fromOfficial.WeeklyWeekCount == 4, "官方旧格式文件（缺周数字段）应取默认值 4");
        store.Save(store.Load());
        using var saved = JsonDocument.Parse(File.ReadAllText(store.StatePath));
        Assert(
            saved.RootElement.GetProperty("SchemaVersion").GetInt32() == AppState.CurrentSchemaVersion,
            "旧状态保存后应使用当前版本");
        Assert(
            !saved.RootElement.GetProperty("TodoWidgets")[0].GetProperty("Items")[0].TryGetProperty("Date", out var dateProperty)
                || dateProperty.ValueKind == JsonValueKind.Null,
            "官方数据保存后不应写入有值的日期字段");
        using var backup = JsonDocument.Parse(File.ReadAllText(store.StatePath + ".bak"));
        Assert(backup.RootElement.GetProperty("SchemaVersion").GetInt32() > 33
            && backup.RootElement.GetProperty("TodoWidgets")[0].GetProperty("Items")[0]
                .GetProperty("Text").GetString() == "官方待办",
            "上一份有效状态应保留内容并升级备份格式，阻止旧版回退读取 v33 备份");

        // Existing weekly-view v33 files must also preserve their calendar data.
        store.Save(state);
        var weeklyV33 = File.ReadAllText(store.StatePath).Replace(
            $"\"SchemaVersion\": {AppState.CurrentSchemaVersion}", "\"SchemaVersion\": 33");
        File.WriteAllText(store.StatePath, weeklyV33);
        var migrated = store.Load();
        var migratedItem = migrated.TodoWidgets.Single().Items.Single(x => x.Id == dated.Id);
        Assert(migrated.SchemaVersion == AppState.CurrentSchemaVersion
            && migrated.TodoWidgets.Single().WeeklyView
            && migrated.TodoWidgets.Single().WeeklyWeekCount == 2
            && migratedItem.Date == dated.Date
            && migratedItem.CreatedDate == dated.CreatedDate
            && migratedItem.CompletedDate == dated.CompletedDate,
            "迁移周历 v33 文件应保留日期、时间戳和视图设置");
        File.WriteAllText(store.StatePath + ".bak", weeklyV33);
        File.WriteAllText(store.StatePath, "");
        store.Save(store.Load());
        using var recoveredBackup = JsonDocument.Parse(File.ReadAllText(store.StatePath + ".bak"));
        Assert(recoveredBackup.RootElement.GetProperty("SchemaVersion").GetInt32() == AppState.CurrentSchemaVersion
            && store.Load().TodoWidgets.Single().Items.Single(x => x.Id == dated.Id).Date == dated.Date,
            "从 v33 备份恢复后，主文件和有效备份均应升级且保留日期");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestLunarDayText()
{
    // 2026-02-17 是农历正月初一（春节），随后两天依次推算。
    Assert(LunarDayText.For(new DateTime(2026, 2, 17)) == "正月", "春节应显示月名正月");
    Assert(LunarDayText.For(new DateTime(2026, 2, 18)) == "初二", "春节次日应为初二");
    Assert(LunarDayText.For(new DateTime(2026, 3, 4)) == "十六", "春节后十五天应为正月十六");
    // 2025 年有闰六月，2025-07-25 为闰六月初一（外部可查证锚点）。
    Assert(LunarDayText.For(new DateTime(2025, 7, 25)) == "闰六月", "闰月初一应显示闰月月名");
    Assert(LunarDayText.For(new DateTime(2025, 7, 26)) == "初二", "闰月初二应显示日名");
    Assert(LunarDayText.For(new DateTime(1900, 1, 1)) == "", "历法范围外不应抛异常");
    Assert(LunarDayText.For(new DateTime(2150, 1, 1)) == "", "历法上限外不应抛异常");
}

static void TestTopBarTodayCount()
{
    var widget = new TodoWidgetState();
    var today = DateTime.Today;
    TodoService.Add(widget, "无日期未完成");
    TodoService.Add(widget, "今日格子", today);
    TodoService.Add(widget, "明日格子", today.AddDays(1));
    var done = TodoService.Add(widget, "今日已完成", today);
    done.IsCompleted = true;
    Assert(TopBarModuleRules.CountIncompleteTodosToday([widget]) == 2,
        "顶栏应只统计无日期与今日的未完成待办");
    Assert(TopBarModuleRules.CountIncompleteTodosToday([]) == 0, "无待办卡计 0");
}

static void TestDesktopClassification()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-desktop-classify-{Guid.NewGuid():N}");
    var stateDirectory = Path.Combine(temp, "State");
    Directory.CreateDirectory(temp);

    try
    {
        var shortcut = Path.Combine(temp, "浏览器.lnk");
        var website = Path.Combine(temp, "工作台.url");
        var executable = Path.Combine(temp, "工具.exe");
        var folder = Path.Combine(temp, "项目资料");
        var document = Path.Combine(temp, "计划.docx");
        File.WriteAllText(shortcut, "shortcut");
        File.WriteAllText(website, "url");
        File.WriteAllText(executable, "executable");
        Directory.CreateDirectory(folder);
        File.WriteAllText(document, "document");

        var groups = FileMappingService.ClassifyDesktopEntries(
            [shortcut, website, executable, folder, document, shortcut, Path.Combine(temp, "missing")]);
        Assert(
            groups[DesktopGroupCategory.Applications]
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals([shortcut, website, executable]),
            "软件与快捷入口分类错误");
        Assert(groups[DesktopGroupCategory.Folders].SequenceEqual([folder]),
            "桌面文件夹分类错误");
        Assert(groups[DesktopGroupCategory.Files].SequenceEqual([document]),
            "桌面文件分类错误");

        var boxes = groups.Select(pair =>
        {
            var box = new FileBoxState { DesktopCategory = pair.Key };
            FileMappingService.AddExisting(box, pair.Value);
            return box;
        }).ToList();
        var store = new LocalStateStore(stateDirectory);
        store.Save(new AppState { FileBoxes = boxes });
        var restored = store.Load();

        Assert(restored.SchemaVersion == AppState.CurrentSchemaVersion, "分类状态未保存为当前 Schema");
        Assert(
            restored.FileBoxes.Select(box => box.DesktopCategory).ToHashSet()
                .SetEquals([
                    DesktopGroupCategory.Applications,
                    DesktopGroupCategory.Folders,
                    DesktopGroupCategory.Files
                ]),
            "分类盒身份重启后未恢复");
        Assert(File.Exists(shortcut) && File.Exists(website) && File.Exists(executable) &&
               Directory.Exists(folder) && File.Exists(document),
            "桌面分类不应移动或删除原文件");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestMappings()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-map-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var file = Path.Combine(temp, "示例.txt");
        File.WriteAllText(file, "test");
        var box = new FileBoxState();
        Assert(FileMappingService.AddExisting(box, [file, file]) == 1, "重复路径不应重复添加");
        Assert(File.Exists(file), "映射模式不应移动或删除原文件");
        Assert(FileMappingService.Remove(box, box.Items[0].Id), "映射应可移除");
        Assert(File.Exists(file), "移除映射不应删除原文件");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestDesktopGrouping()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-desktop-group-{Guid.NewGuid():N}");
    var userDesktop = Path.Combine(temp, "UserDesktop");
    var commonDesktop = Path.Combine(temp, "CommonDesktop");
    var stateDirectory = Path.Combine(temp, "State");
    Directory.CreateDirectory(userDesktop);
    Directory.CreateDirectory(commonDesktop);

    try
    {
        var textFile = Path.Combine(userDesktop, "Zeta.txt");
        var shortcut = Path.Combine(userDesktop, "Alpha.lnk");
        var folder = Path.Combine(userDesktop, "资料");
        var url = Path.Combine(commonDesktop, "Beta.url");
        File.WriteAllText(textFile, "桌面文档内容");
        File.WriteAllText(shortcut, "快捷方式占位内容");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "不应递归导入.txt"), "嵌套内容");
        File.WriteAllText(url, "URL 快捷方式内容");
        var hiddenDesktopFile = Path.Combine(userDesktop, "desktop.ini");
        File.WriteAllText(hiddenDesktopFile, "隐藏桌面配置");
        File.SetAttributes(
            hiddenDesktopFile,
            File.GetAttributes(hiddenDesktopFile) | FileAttributes.Hidden);
        var systemDesktopFile = Path.Combine(commonDesktop, "系统桌面配置.dat");
        File.WriteAllText(systemDesktopFile, "系统桌面配置");
        File.SetAttributes(
            systemDesktopFile,
            File.GetAttributes(systemDesktopFile) | FileAttributes.System);

        var originalFileContents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [textFile] = File.ReadAllText(textFile),
            [shortcut] = File.ReadAllText(shortcut),
            [url] = File.ReadAllText(url)
        };
        var originalPaths = new[] { textFile, shortcut, folder, url }
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var box = new FileBoxState();
        Assert(FileMappingService.AddExisting(box, [textFile]) == 1, "预置映射失败");
        var discovered = FileMappingService.EnumerateDirectChildren(
            [userDesktop, userDesktop, commonDesktop, Path.Combine(temp, "Missing")]);
        Assert(discovered.Count == 4, "桌面根目录去重或直接子项读取错误");
        Assert(
            !discovered.Contains(hiddenDesktopFile, StringComparer.OrdinalIgnoreCase),
            "隐藏桌面项目不应导入分组盒");
        Assert(
            !discovered.Contains(systemDesktopFile, StringComparer.OrdinalIgnoreCase),
            "系统桌面项目不应导入分组盒");
        Assert(FileMappingService.AddExisting(box, discovered) == 3, "已有桌面映射未正确去重");
        Assert(box.Items.Count == 4, "导入桌面项目后的映射总数错误");

        FileMappingService.Sort(box, FileMappingSortMode.Name);
        var nameOrder = box.Items.OrderBy(item => item.Order).Select(item => item.Path).ToArray();
        var expectedNameOrder = originalPaths
            .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert(nameOrder.SequenceEqual(expectedNameOrder), "按名称排序结果错误");

        FileMappingService.Sort(box, FileMappingSortMode.Type);
        var typeOrder = box.Items.OrderBy(item => item.Order).Select(item => item.Path).ToArray();
        Assert(typeOrder[0] == folder, "按类型排序应优先显示文件夹");
        Assert(
            typeOrder.Skip(1).Select(Path.GetExtension).SequenceEqual([".lnk", ".txt", ".url"]),
            "按类型排序未按扩展名分组");

        var last = box.Items.Single(item => item.Path == url);
        Assert(FileMappingService.Move(box, last.Id, -1), "手动顺序无法上移映射");
        var manualOrder = box.Items.OrderBy(item => item.Order).Select(item => item.Path).ToArray();
        FileMappingService.Sort(box, FileMappingSortMode.Manual);
        Assert(
            box.Items.OrderBy(item => item.Order).Select(item => item.Path).SequenceEqual(manualOrder),
            "选择手动顺序不应改写现有顺序");

        var store = new LocalStateStore(stateDirectory);
        store.Save(new AppState { FileBoxes = [box] });
        var restoredOrder = store.Load().FileBoxes.Single().Items
            .OrderBy(item => item.Order)
            .Select(item => item.Path);
        Assert(restoredOrder.SequenceEqual(manualOrder), "桌面分组排序重启后未保留");
        Assert(
            box.Items.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(originalPaths),
            "导入或排序改变了映射路径");
        Assert(Directory.Exists(folder), "导入或排序删除了原文件夹");
        foreach (var (path, content) in originalFileContents)
        {
            Assert(File.Exists(path), $"导入或排序删除了原文件：{path}");
            Assert(File.ReadAllText(path) == content, $"导入或排序改变了原文件内容：{path}");
        }
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestPersistence()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-state-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStateStore(temp);
        var state = LocalStateStore.CreateDefault();
        var todo = state.TodoWidgets[0];
        todo.Title = "持久化测试";
        todo.Placement.Left = 123;
        todo.Placement.Top = 234;
        todo.Placement.Width = 345;
        todo.Placement.Height = 456;
        todo.CornerRadius = 3;
        todo.Appearance.BackgroundColor = "#C1D2E3";
        todo.Appearance.BackgroundOpacity = 0.45;
        todo.Appearance.HeaderColor = "#91A2B3";
        todo.Appearance.HeaderOpacity = 0.88;
        todo.Appearance.TextColor = "#FFFFFF";
        todo.Appearance.TitleFontFamily = "Segoe UI Variable Display";
        todo.Appearance.TitleFontBold = false;
        todo.Appearance.TitleFontItalic = true;
        var persisted = TodoService.Add(todo, "持久化任务");
        persisted.IsCompleted = true;
        var box = state.FileBoxes.Single();
        box.CornerRadius = 32;
        box.Appearance.BackgroundColor = "#D4E5F6";
        box.Appearance.BackgroundOpacity = 0.25;
        box.Appearance.TextColor = "#000000";
        box.Appearance.TitleFontFamily = "Microsoft JhengHei UI";
        box.Appearance.TitleFontBold = true;
        box.Appearance.TitleFontItalic = false;
        store.Save(state);

        var loaded = store.Load();
        var restored = loaded.TodoWidgets.Single();
        Assert(restored.Title == "持久化测试", "标题未恢复");
        Assert(restored.Placement.Left == 123 && restored.Placement.Height == 456, "位置或尺寸未恢复");
        Assert(restored.CornerRadius == 3, "待办圆角未恢复");
        Assert(
            restored.Appearance.BackgroundColor == "#C1D2E3" &&
            restored.Appearance.BackgroundOpacity == 0.45 &&
            restored.Appearance.HeaderColor == "#91A2B3" &&
            restored.Appearance.HeaderOpacity == 0.88 &&
            restored.Appearance.TextColor == "#FFFFFF" &&
            restored.Appearance.TitleFontFamily == "Segoe UI Variable Display" &&
            !restored.Appearance.TitleFontBold &&
            restored.Appearance.TitleFontItalic,
            "待办材质外观未恢复");
        Assert(loaded.FileBoxes.Single().CornerRadius == 32, "文件盒圆角未恢复");
        Assert(
            loaded.FileBoxes.Single().Appearance.BackgroundColor == "#D4E5F6" &&
            loaded.FileBoxes.Single().Appearance.BackgroundOpacity == 0.25 &&
            loaded.FileBoxes.Single().Appearance.TextColor == "#000000" &&
            loaded.FileBoxes.Single().Appearance.TitleFontFamily == "Microsoft JhengHei UI" &&
            loaded.FileBoxes.Single().Appearance.TitleFontBold &&
            !loaded.FileBoxes.Single().Appearance.TitleFontItalic,
            "文件盒材质外观未恢复");
        Assert(loaded.SchemaVersion == AppState.CurrentSchemaVersion, "状态版本未更新");
        Assert(restored.Items.Count == 1 && restored.Items[0].IsCompleted
            && restored.Items[0].Text == "持久化任务", "待办条目与完成状态未恢复");
        Assert(File.Exists(store.StatePath), "状态文件不存在");
        Assert(File.Exists(store.CreateBackup(loaded)), "备份未创建");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static void TestCornerRadiusMigration()
{
    var directTodo = new TodoWidgetState { CornerRadius = -1 };
    var directBox = new FileBoxState { CornerRadius = 100 };
    Assert(directTodo.CornerRadius == 0, "负圆角应钳制为 0");
    Assert(directBox.CornerRadius == 32, "过大圆角应钳制为 32");
    directTodo.CornerRadius = double.NaN;
    directBox.CornerRadius = double.PositiveInfinity;
    Assert(directTodo.CornerRadius == 15.5, "NaN 圆角应恢复默认值");
    Assert(directBox.CornerRadius == 15.5, "无限圆角应恢复默认值");

    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-migration-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 1,
              "TodoWidgets": [
                {
                  "Title": "旧待办",
                  "Placement": { "Left": 80, "Top": 80, "Width": 310, "Height": 360 },
                  "Items": []
                }
              ],
              "FileBoxes": [
                {
                  "Title": "旧文件盒",
                  "Placement": { "Left": 420, "Top": 80, "Width": 390, "Height": 330 },
                  "Items": []
                }
              ]
            }
            """);

        var migrated = store.Load();
        Assert(migrated.SchemaVersion == AppState.CurrentSchemaVersion,
            "v1 状态未迁移到当前版本");
        Assert(migrated.TodoWidgets.Single().CornerRadius == 15.5, "旧待办未获得默认圆角");
        Assert(migrated.FileBoxes.Single().CornerRadius == 15.5, "旧文件盒未获得默认圆角");
        Assert(
            migrated.TodoWidgets.Single().Placement.Left == 92 &&
            migrated.TodoWidgets.Single().Placement.Width == 286 &&
            migrated.FileBoxes.Single().Placement.Left == 432 &&
            migrated.FileBoxes.Single().Placement.Width == 366,
            "旧分层窗口几何未移除 12 DIP 阴影留白");

        store.Save(migrated);
        var backupPath = store.ListBackups().Single(path =>
            Path.GetFileName(path).StartsWith("widgets-before-migration-", StringComparison.Ordinal));
        Assert(File.Exists(backupPath), "迁移保存前未保留 v1 备份");
        using var backup = JsonDocument.Parse(File.ReadAllText(backupPath));
        Assert(
            backup.RootElement.GetProperty("SchemaVersion").GetInt32() == 1,
            "迁移备份不再是原始 v1 状态");
        using var currentBackup = JsonDocument.Parse(File.ReadAllText(store.StatePath + ".bak"));
        Assert(currentBackup.RootElement.GetProperty("SchemaVersion").GetInt32() == AppState.CurrentSchemaVersion,
            "上一份有效状态备份应使用当前格式");

        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 2,
              "TodoWidgets": [
                {
                  "Title": "中间版本",
                  "CornerRadius": 3,
                  "Placement": { "Left": 100, "Top": 100, "Width": 310, "Height": 360 },
                  "Items": []
                }
              ],
              "FileBoxes": []
            }
            """);
        var migratedV2 = store.Load();
        Assert(migratedV2.SchemaVersion == AppState.CurrentSchemaVersion,
            "v2 状态未迁移到当前版本");
        Assert(migratedV2.TodoWidgets.Single().CornerRadius == 3, "v2 圆角在几何迁移中丢失");
        Assert(
            migratedV2.TodoWidgets.Single().Placement.Left == 112 &&
            migratedV2.TodoWidgets.Single().Placement.Width == 286,
            "v2 状态未迁移旧阴影留白");

        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 2,
              "TodoWidgets": [
                {
                  "Title": "旧最小尺寸",
                  "Placement": { "Left": 40, "Top": 40, "Width": 250, "Height": 190 },
                  "Items": []
                }
              ],
              "FileBoxes": []
            }
            """);
        var migratedMinimum = store.Load().TodoWidgets.Single().Placement;
        Assert(
            migratedMinimum.Left == 52 &&
            migratedMinimum.Top == 52 &&
            migratedMinimum.Width == 226 &&
            migratedMinimum.Height == 166,
            "v2 最小尺寸迁移后没有保持原可见主体大小");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestAppearanceMigration()
{
    var appearance = new WidgetAppearanceState();
    Assert(appearance.BackgroundColor == "#FFFFFF", "主体颜色默认值错误");
    Assert(appearance.BackgroundOpacity == 0.32, "主体透明度默认值错误");
    Assert(appearance.HeaderColor == "#A0B4E1", "标题栏颜色默认值错误");
    Assert(appearance.HeaderOpacity == 0.97, "标题栏透明度默认值错误");
    Assert(appearance.TextColor == "#243146", "卡片文字颜色默认值错误");
    Assert(appearance.TitleFontFamily == "Microsoft YaHei UI", "标题字体默认值错误");
    Assert(appearance.TitleFontBold, "标题默认应为粗体");
    Assert(!appearance.TitleFontItalic, "标题默认不应为斜体");

    appearance.BackgroundColor = "#a1b2c3";
    appearance.HeaderColor = "#102aBc";
    appearance.TextColor = "#fFfFfF";
    Assert(appearance.BackgroundColor == "#A1B2C3", "主体颜色未规范为大写 #RRGGBB");
    Assert(appearance.HeaderColor == "#102ABC", "标题栏颜色未规范为大写 #RRGGBB");
    Assert(appearance.TextColor == "#FFFFFF", "文字颜色未规范为大写 #RRGGBB");

    appearance.BackgroundColor = "#12345";
    appearance.HeaderColor = "#GGGGGG";
    appearance.TextColor = "black";
    Assert(appearance.BackgroundColor == "#FFFFFF", "非法主体颜色未回退默认值");
    Assert(appearance.HeaderColor == "#A0B4E1", "非法标题栏颜色未回退默认值");
    Assert(appearance.TextColor == "#243146", "非法文字颜色未回退默认值");

    appearance.BackgroundOpacity = -0.1;
    appearance.HeaderOpacity = 1.1;
    Assert(
        appearance.BackgroundOpacity == WidgetAppearanceRules.MinimumBackgroundOpacity,
        "主体透明度未钳制到可拖动下限");
    Assert(appearance.HeaderOpacity == 1, "标题栏透明度未钳制到 1");
    appearance.BackgroundOpacity = double.NaN;
    appearance.HeaderOpacity = double.PositiveInfinity;
    Assert(appearance.BackgroundOpacity == 0.32, "NaN 主体透明度未回退默认值");
    Assert(appearance.HeaderOpacity == 0.97, "无限标题栏透明度未回退默认值");

    appearance.TitleFontFamily = "  Segoe UI Variable Display  ";
    Assert(appearance.TitleFontFamily == "Segoe UI Variable Display", "标题字体名未去除首尾空白");
    appearance.TitleFontFamily = "Bad\nFont";
    Assert(appearance.TitleFontFamily == "Microsoft YaHei UI", "含控制字符的标题字体名未回退默认值");
    appearance.TitleFontFamily = new string('A', WidgetAppearanceRules.MaximumTitleFontFamilyLength + 1);
    Assert(appearance.TitleFontFamily == "Microsoft YaHei UI", "过长标题字体名未回退默认值");

    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-appearance-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 3,
              "TodoWidgets": [
                {
                  "Title": "v3 待办",
                  "Placement": { "Left": 92, "Top": 92, "Width": 286, "Height": 336 },
                  "Items": []
                }
              ],
              "FileBoxes": [
                {
                  "Title": "v3 文件盒",
                  "Placement": { "Left": 432, "Top": 92, "Width": 366, "Height": 306 },
                  "Items": []
                }
              ]
            }
            """);

        var migrated = store.Load();
        var todo = migrated.TodoWidgets.Single();
        var box = migrated.FileBoxes.Single();
        Assert(migrated.SchemaVersion == AppState.CurrentSchemaVersion,
            "v3 状态未迁移到当前版本");
        Assert(
            todo.Appearance.BackgroundColor == "#FFFFFF" &&
            todo.Appearance.BackgroundOpacity == 0.32 &&
            todo.Appearance.HeaderColor == "#A0B4E1" &&
            todo.Appearance.HeaderOpacity == 0.97 &&
            todo.Appearance.TextColor == "#243146" &&
            todo.Appearance.TitleFontFamily == "Microsoft YaHei UI" &&
            todo.Appearance.TitleFontBold &&
            !todo.Appearance.TitleFontItalic,
            "v3 待办未获得 Pogget 材质默认值");
        Assert(
            box.Appearance.BackgroundColor == "#FFFFFF" &&
            box.Appearance.BackgroundOpacity == 0.32 &&
            box.Appearance.HeaderColor == "#A0B4E1" &&
            box.Appearance.HeaderOpacity == 0.97 &&
            box.Appearance.TextColor == "#243146" &&
            box.Appearance.TitleFontFamily == "Microsoft YaHei UI" &&
            box.Appearance.TitleFontBold &&
            !box.Appearance.TitleFontItalic,
            "v3 文件盒未获得 Pogget 材质默认值");
        Assert(
            todo.Placement.Left == 92 &&
            todo.Placement.Width == 286 &&
            box.Placement.Left == 432 &&
            box.Placement.Width == 366,
            "v3 外观迁移不应再次调整窗口几何");

        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 4,
              "TodoWidgets": [
                {
                  "Title": "v4 待办",
                  "Appearance": {
                    "BackgroundColor": "#C1D2E3",
                    "BackgroundOpacity": 0.45,
                    "HeaderColor": "#91A2B3",
                    "HeaderOpacity": 0.88
                  },
                  "Placement": { "Left": 177, "Top": 188, "Width": 299, "Height": 355 },
                  "Items": []
                }
              ],
              "FileBoxes": [
                {
                  "Title": "v4 文件盒",
                  "Appearance": {
                    "BackgroundColor": "#D4E5F6",
                    "BackgroundOpacity": 0.25,
                    "HeaderColor": "#8192A3",
                    "HeaderOpacity": 0.76
                  },
                  "Placement": { "Left": 511, "Top": 122, "Width": 377, "Height": 311 },
                  "Items": []
                }
              ]
            }
            """);

        var migratedV4 = store.Load();
        var todoV4 = migratedV4.TodoWidgets.Single();
        var boxV4 = migratedV4.FileBoxes.Single();
        Assert(migratedV4.SchemaVersion == AppState.CurrentSchemaVersion,
            "v4 状态未迁移到当前版本");
        Assert(
            todoV4.Appearance.TitleFontFamily == "Microsoft YaHei UI" &&
            todoV4.Appearance.TitleFontBold &&
            !todoV4.Appearance.TitleFontItalic &&
            boxV4.Appearance.TitleFontFamily == "Microsoft YaHei UI" &&
            boxV4.Appearance.TitleFontBold &&
            !boxV4.Appearance.TitleFontItalic,
            "v4 组件未获得标题字体默认值");
        Assert(
            todoV4.Appearance.BackgroundColor == "#C1D2E3" &&
            todoV4.Appearance.BackgroundOpacity == 0.45 &&
            boxV4.Appearance.HeaderColor == "#8192A3" &&
            boxV4.Appearance.HeaderOpacity == 0.76,
            "v4-v5 迁移改变了既有材质设置");
        Assert(
            todoV4.Placement.Left == 177 &&
            todoV4.Placement.Top == 188 &&
            todoV4.Placement.Width == 299 &&
            todoV4.Placement.Height == 355 &&
            boxV4.Placement.Left == 511 &&
            boxV4.Placement.Top == 122 &&
            boxV4.Placement.Width == 377 &&
            boxV4.Placement.Height == 311,
            "v4-v5 标题字体迁移不应再次调整窗口几何");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestNotePersistenceAndMigration()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-note-migration-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 6,
              "TodoWidgets": [],
              "FileBoxes": []
            }
            """);

        var migrated = store.Load();
        Assert(
            migrated.SchemaVersion == AppState.CurrentSchemaVersion,
            "v6 状态未迁移到当前 Schema");
        Assert(migrated.NoteWidgets.Count == 0, "v6 状态不应凭空创建普通便签");
        Assert(migrated.InformationWidgets.Count == 0, "旧状态迁移不应凭空显示桌面信息");

        var note = NoteService.CreateDefaultWidget();
        note.Title = "迁移后的便签";
        NoteService.CommitContent(note, "第一行\r\n第二行");
        note.BodyTypography.FontSize = 18;
        note.BodyTypography.Color = "#10203a";
        note.BodyTypography.Bold = true;
        note.BodyTypography.Alignment = NoteBodyTextAlignment.Right;
        migrated.NoteWidgets.Add(note);
        store.Save(migrated);

        var restored = store.Load().NoteWidgets.Single();
        Assert(restored.Title == "迁移后的便签", "普通便签标题未持久化");
        Assert(restored.Content == "第一行\n第二行", "普通便签多行正文未持久化");
        Assert(
            restored.BodyTypography.FontSize == 18 &&
            restored.BodyTypography.Color == "#10203A" &&
            restored.BodyTypography.Bold &&
            restored.BodyTypography.Alignment == NoteBodyTextAlignment.Right,
            "普通便签正文排版未持久化");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestInformationPersistenceAndMigration()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-information-migration-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 7,
              "TodoWidgets": [],
              "NoteWidgets": [],
              "FileBoxes": []
            }
            """);

        var migrated = store.Load();
        Assert(
            migrated.SchemaVersion == AppState.CurrentSchemaVersion,
            "v7 状态未迁移到当前 Schema");
        Assert(migrated.InformationWidgets.Count == 0, "v7 状态不应凭空显示桌面信息");

        var information = new InformationWidgetState
        {
            Title = "  主屏信息  ",
            GreetingName = "  小明  ",
            Use24HourClock = false,
            WeatherCity = "  北京  ",
            WeatherLatitude = 39.9,
            WeatherLongitude = 116.4,
            CornerRadius = 3
        };
        migrated.InformationWidgets.Add(information);
        store.Save(migrated);

        var restored = store.Load().InformationWidgets.Single();
        Assert(restored.Title == "主屏信息", "桌面信息标题未规范化或持久化");
        Assert(restored.GreetingName == "小明", "问候称呼未规范化或持久化");
        Assert(!restored.Use24HourClock, "12/24 小时设置未持久化");
        Assert(
            restored.WeatherCity == "北京" &&
            restored.WeatherLatitude == 39.9 &&
            restored.WeatherLongitude == 116.4,
            "用户选择的天气城市与坐标未持久化");
        Assert(restored.CornerRadius == 3, "桌面信息圆角未持久化");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestDesktopExperiencePersistence()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-experience-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 9,
              "TodoWidgets": [],
              "NoteWidgets": [],
              "InformationWidgets": [],
              "FileBoxes": []
            }
            """);

        var migrated = store.Load();
        Assert(
            migrated.SchemaVersion == AppState.CurrentSchemaVersion,
            "v9 状态未迁移到当前 Schema");
        Assert(
            migrated.DesktopExperience.Components.Count ==
            DesktopExperienceRules.ComponentOrder.Count,
            "迁移后应补齐全部内置组件状态");
        Assert(
            migrated.DesktopExperience.GetComponent(DesktopComponentKind.TimeDate)
                .Appearance.TextColor == "#FFFFFF" &&
            migrated.DesktopExperience.GetComponent(DesktopComponentKind.Greeting)
                .Appearance.TextColor == "#FFFFFF",
            "旧默认时间/问候文字色未迁移为白色");

        migrated.DesktopExperience.ActivePreset = DesktopLayoutPreset.TransparentApple;
        migrated.DesktopExperience.GetComponent(DesktopComponentKind.Todo).IsVisible = false;
        var audio = migrated.DesktopExperience
            .GetComponent(DesktopComponentKind.AudioVisualizer);
        audio.IsVisible = true;
        audio.FontScale = 9;
        store.Save(migrated);

        var restored = store.Load().DesktopExperience;
        Assert(
            restored.ActivePreset == DesktopLayoutPreset.TransparentApple,
            "布局预设未持久化");
        Assert(
            !restored.GetComponent(DesktopComponentKind.Todo).IsVisible &&
            restored.GetComponent(DesktopComponentKind.AudioVisualizer).IsVisible,
            "组件显示开关未持久化");
        Assert(
            restored.GetComponent(DesktopComponentKind.AudioVisualizer).FontScale ==
            DesktopExperienceRules.MaximumFontScale,
            "组件字号缩放未规范化");

        File.WriteAllText(
            store.StatePath,
            """
            {
              "SchemaVersion": 11,
              "TodoWidgets": [],
              "NoteWidgets": [],
              "InformationWidgets": [],
              "FileBoxes": [],
              "DesktopExperience": {
                "ActivePreset": 0,
                "IsLayoutEditing": false,
                "Components": [
                  { "Kind": 0, "Appearance": { "TextColor": "#FF3366" } },
                  { "Kind": 1, "Appearance": { "TextColor": "#243146" } }
                ]
              }
            }
            """);
        var custom = store.Load().DesktopExperience;
        Assert(
            custom.GetComponent(DesktopComponentKind.TimeDate)
                .Appearance.TextColor == "#FF3366",
            "v12 迁移不应覆盖用户自定义时间文字色");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestTaskbarCompatibility()
{
    var ready = new TaskbarEnvironment(26200, false, false, true, null);
    Assert(TaskbarCompatibility.Assess(TaskbarMode.Transparent, ready).IsSupported, "Windows 11 应支持透明");
    Assert(TaskbarCompatibility.Assess(TaskbarMode.SmartHide, ready).IsSupported, "Windows 11 应支持自动隐藏");
    Assert(TaskbarCompatibility.Assess(TaskbarMode.Blur, ready).IsSupported, "Windows 11 应支持模糊");
    Assert(!TaskbarCompatibility.Assess(TaskbarMode.Transparent, ready with { WindowsBuild = 26100 }).IsSupported,
        "未验证的旧 XAML 结构不应启用透明");
    Assert(!TaskbarCompatibility.Assess(TaskbarMode.Transparent, ready with { WindowsBuild = 30000 }).IsSupported,
        "未来 Windows 版本不应自动进入透明适配");
    Assert(TaskbarCompatibility.Assess(TaskbarMode.SmartHide, ready with { WindowsBuild = 30000 }).IsSupported,
        "透明适配的版本限制不应阻断系统自动隐藏");
    Assert(
        !TaskbarCompatibility.Assess(TaskbarMode.Blur, ready with { IsSystemTransparencyEnabled = false }).IsSupported,
        "系统透明效果关闭时不应模糊");
    Assert(
        TaskbarCompatibility.Assess(TaskbarMode.SystemDefault, ready with { WindowsBuild = 19045 }).IsSupported,
        "系统默认在任何环境都可用");
    Assert(
        !TaskbarCompatibility.Assess(TaskbarMode.SmartHide, ready with { WindowsBuild = 19045 }).IsSupported,
        "Windows 10 不在支持范围");
    Assert(
        !TaskbarCompatibility.Assess(TaskbarMode.Transparent, ready with { IsHighContrast = true }).IsSupported,
        "对比度主题下不应透明");
    Assert(
        TaskbarCompatibility.Assess(TaskbarMode.SmartHide, ready with { IsHighContrast = true }).IsSupported,
        "对比度主题不影响自动隐藏");
    Assert(
        !TaskbarCompatibility.Assess(TaskbarMode.Transparent, ready with { IsRemoteSession = true }).IsSupported,
        "远程桌面中不应透明");
    Assert(
        !TaskbarCompatibility.Assess(TaskbarMode.Transparent, ready with { IsSystemTransparencyEnabled = false }).IsSupported,
        "系统透明效果关闭时不应透明");
    var competing = TaskbarCompatibility.Assess(
        TaskbarMode.SmartHide,
        ready with { CompetingCustomizer = "TranslucentTB" });
    Assert(!competing.IsSupported && competing.Reason.Contains("TranslucentTB"), "其他任务栏工具运行时应让出");
}

static void TestTaskbarRecovery()
{
    var transparent = new TaskbarCheckpoint { TransparencyApplied = true };
    var parsed = TaskbarCheckpoint.Parse(transparent.Serialize());
    Assert(parsed is { TransparencyApplied: true, AutoHideApplied: false }, "检查点应往返保存");
    Assert(TaskbarCheckpoint.Parse("""{"SchemaVersion":1,"TransparencyApplied":true}""")
        is { TransparencyApplied: true, XamlSessionName: null }, "旧窗口材质检查点仍可恢复");
    var xaml = new TaskbarCheckpoint { XamlSessionName = @"Local\BingLan.Taskbar.Xaml.0123456789abcdef0123456789abcdef" };
    Assert(TaskbarCheckpoint.Parse(xaml.Serialize())?.XamlSessionName == xaml.XamlSessionName,
        "XAML 恢复记录保留会话身份");
    Assert(!TaskbarRecovery.Plan(xaml, currentAutoHide: false).ResetTransparency,
        "XAML 透明不应重置没有改过的旧窗口材质");
    Assert(TaskbarCheckpoint.Parse("{not json") is null, "损坏的检查点应被忽略");
    Assert(TaskbarCheckpoint.Parse("""{"SchemaVersion": 99}""") is null, "未来版本的检查点应被忽略");

    var plan = TaskbarRecovery.Plan(transparent, currentAutoHide: false);
    Assert(plan.ResetTransparency && plan.SetAutoHide is null, "透明检查点只恢复外观");

    var autoHide = new TaskbarCheckpoint { AutoHideApplied = true, OriginalAutoHide = false };
    Assert(
        TaskbarRecovery.Plan(autoHide, currentAutoHide: true).SetAutoHide == false,
        "由本程序开启的自动隐藏应被关闭");
    Assert(
        TaskbarRecovery.Plan(autoHide, currentAutoHide: false).SetAutoHide is null,
        "用户已自行关闭自动隐藏时不应再改动");
    Assert(
        TaskbarRecovery.Plan(new TaskbarCheckpoint(), currentAutoHide: true) is { ResetTransparency: false, SetAutoHide: null },
        "空检查点不应改动任务栏");
}

static void TestTaskbarBreaker()
{
    var breaker = new TaskbarFailureBreaker(3);
    Assert(!breaker.RecordFailure() && !breaker.RecordFailure(), "前两次失败不应熔断");
    breaker.RecordSuccess();
    Assert(breaker.ConsecutiveFailures == 0, "成功后应清零");
    breaker.RecordFailure();
    breaker.RecordFailure();
    Assert(breaker.RecordFailure() && breaker.IsTripped, "连续三次失败应熔断");
}

static void TestOnboardingFlagMigration()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-onboarding-{Guid.NewGuid():N}");
    try
    {
        var fresh = new LocalStateStore(temp).Load();
        Assert(!fresh.OnboardingCompleted, "新安装应显示首次引导");

        var store = new LocalStateStore(temp);
        File.WriteAllText(store.StatePath, """{ "SchemaVersion": 14 }""");
        var upgraded = store.Load();
        Assert(upgraded.OnboardingCompleted && upgraded.SchemaVersion == AppState.CurrentSchemaVersion,
            "升级用户不应再看到首次引导");

        upgraded.OnboardingCompleted = false;
        store.Save(upgraded);
        Assert(!store.Load().OnboardingCompleted, "当前版本的引导状态应按原值保存");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static void TestBackupRestore()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-restore-{Guid.NewGuid():N}");
    try
    {
        var store = new LocalStateStore(temp);
        var state = store.Load();
        state.TodoWidgets[0].Title = "备份时的标题";
        var first = store.CreateBackup(state);
        var second = store.CreateBackup(state);
        Assert(first != second, "同一秒内的两次备份不应互相覆盖");
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-5));
        var listed = store.ListBackups();
        Assert(listed.Count == 2 && listed[0] == second, "备份列表应按时间倒序");

        state.TodoWidgets[0].Title = "恢复前的标题";
        store.Save(state);
        store.ScheduleRestore(first);
        Assert(File.Exists(store.PendingRestorePath), "恢复应先排队，等待下次启动");
        Assert(store.Load().TodoWidgets[0].Title == "备份时的标题", "下次加载应使用备份内容");
        Assert(!File.Exists(store.PendingRestorePath), "恢复完成后应删除排队文件");
        Assert(
            store.ListBackups().Any(path => Path.GetFileName(path).StartsWith("widgets-before-restore-", StringComparison.Ordinal)),
            "恢复前应另存当前状态");

        var invalid = Path.Combine(temp, "invalid.json");
        File.WriteAllText(invalid, "{ not json");
        var rejected = false;
        try
        {
            store.ScheduleRestore(invalid);
        }
        catch (JsonException)
        {
            rejected = true;
        }
        Assert(rejected && !File.Exists(store.PendingRestorePath), "损坏的备份应被拒绝且不排队");

        File.WriteAllText(store.PendingRestorePath, "{ not json");
        Assert(store.Load().TodoWidgets[0].Title == "备份时的标题", "损坏的排队文件应被忽略");
        Assert(!File.Exists(store.PendingRestorePath), "损坏的排队文件应被清除");
    }
    finally
    {
        if (Directory.Exists(temp))
        {
            Directory.Delete(temp, true);
        }
    }
}

static void TestDesktopModes()
{
    var dock = new DockState { VisibilityMode = DockVisibilityMode.SmartHide };
    var taskbar = new TaskbarState();
    Assert(DesktopModeRules.Detect(dock, taskbar) == DesktopMode.WindowsNative, "默认应为 Windows 原生");

    dock.PinnedApps.Add(new DockPinnedApp { DisplayName = "编辑器", ExecutablePath = @"C:\Apps\Editor.exe" });
    DesktopModeRules.Apply(DesktopMode.IceBlueHybrid, dock, taskbar);
    Assert(dock.IsEnabled && taskbar.Mode == TaskbarMode.Transparent, "冰蓝混合应开启 Dock 并透明任务栏");
    Assert(dock.VisibilityMode == DockVisibilityMode.ReserveWorkArea, "模式应写入 Dock 的安全默认显示方式");
    Assert(dock.PinnedApps.Count == 1, "切换模式不应改变固定应用");
    Assert(DesktopModeRules.Detect(dock, taskbar) == DesktopMode.IceBlueHybrid, "应识别冰蓝混合");

    DesktopModeRules.Apply(DesktopMode.AppleStyle, dock, taskbar);
    Assert(taskbar.Mode == TaskbarMode.SmartHide && dock.IsEnabled, "苹果式应让任务栏自动隐藏");

    taskbar.Mode = TaskbarMode.Blur;
    Assert(DesktopModeRules.Detect(dock, taskbar) == DesktopMode.IceBlueHybrid, "Dock 加模糊任务栏也属于冰蓝混合");

    taskbar.Mode = TaskbarMode.SystemDefault;
    Assert(DesktopModeRules.Detect(dock, taskbar) is null, "单独调整后应识别为自定义组合");

    DesktopModeRules.Apply(DesktopMode.WindowsNative, dock, taskbar);
    Assert(!dock.IsEnabled && taskbar.Mode == TaskbarMode.SystemDefault, "Windows 原生应关闭 Dock 并恢复任务栏");
}

static void TestTaskbarPersistence()
{
    var temp = Path.Combine(Path.GetTempPath(), $"BingLan-taskbar-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temp);
    try
    {
        var store = new LocalStateStore(temp);
        File.WriteAllText(store.StatePath, """{ "SchemaVersion": 13, "Dock": { "IsEnabled": true } }""");
        var migrated = store.Load();
        Assert(migrated.SchemaVersion == AppState.CurrentSchemaVersion, "v13 状态应迁移到当前版本");
        Assert(migrated.Taskbar.Mode == TaskbarMode.SystemDefault, "迁移后任务栏应保持系统默认");
        Assert(migrated.Dock.IsEnabled, "迁移不应改变 Dock 设置");

        migrated.Taskbar.Mode = TaskbarMode.SmartHide;
        store.Save(migrated);
        Assert(store.Load().Taskbar.Mode == TaskbarMode.SmartHide, "任务栏模式应保存");

        File.WriteAllText(store.StatePath, """{ "SchemaVersion": 14, "Taskbar": { "Mode": 42 } }""");
        Assert(store.Load().Taskbar.Mode == TaskbarMode.SystemDefault, "未知模式应回到系统默认");
    }
    finally
    {
        Directory.Delete(temp, true);
    }
}

static void TestThemeExportExcludesPersonalInfo()
{
    var state = LocalStateStore.CreateDefault();
    state.InformationWidgets[0].GreetingName = "张小明";
    state.InformationWidgets[0].WeatherCity = "杭州市";
    TodoService.Add(state.TodoWidgets[0], "私密待办内容");

    var note = NoteService.CreateDefaultWidget();
    NoteService.CommitContent(note, "私密便签");
    state.NoteWidgets.Add(note);

    state.FileBoxes[0].Items.Add(
        new FileMappingState { Path = @"C:\Users\someone\Desktop\secret.docx" });

    state.Dock.PinnedApps.Add(new DockPinnedApp
    {
        DisplayName = "工具",
        ExecutablePath = @"C:\Users\someone\AppData\Local\Apps\Tool.exe"
    });
    state.Dock.PinnedApps.Add(new DockPinnedApp
    {
        DisplayName = "记事本",
        AppUserModelId = "Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"
    });

    var package = ThemeRules.Export(
        state, "测试主题", new ThemeArea(0, 0, 1920, 1040), new Version(0, 1, 0));

    using var stream = new MemoryStream();
    ThemeArchive.Write(stream, package);
    stream.Position = 0;

    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    var decodedValues = new List<string>();
    foreach (var entry in archive.Entries)
    {
        using var entryStream = entry.Open();
        using var document = JsonDocument.Parse(entryStream);
        decodedValues.AddRange(CollectJsonStrings(document.RootElement));
    }
    var decodedText = string.Join("\n", decodedValues);

    string[] forbidden =
        ["张小明", "杭州市", "私密待办内容", "私密便签", "secret.docx", @"C:\Users", "someone"];
    foreach (var value in forbidden)
    {
        Assert(!decodedText.Contains(value, StringComparison.Ordinal), $"主题导出不应包含：{value}");
    }
    Assert(decodedText.Contains("Tool.exe", StringComparison.Ordinal), "主题导出应保留可执行文件名");
    Assert(
        decodedText.Contains("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", StringComparison.Ordinal),
        "主题导出应保留应用用户模型 ID");
}

static IEnumerable<string> CollectJsonStrings(JsonElement element)
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in element.EnumerateObject())
            {
                yield return property.Name;
                foreach (var value in CollectJsonStrings(property.Value))
                {
                    yield return value;
                }
            }
            break;
        case JsonValueKind.Array:
            foreach (var item in element.EnumerateArray())
            {
                foreach (var value in CollectJsonStrings(item))
                {
                    yield return value;
                }
            }
            break;
        case JsonValueKind.String:
            yield return element.GetString() ?? string.Empty;
            break;
    }
}

static void TestThemePackageRoundTripAndRelativeLayout()
{
    var exportState = LocalStateStore.CreateDefault();
    var timeDate = exportState.DesktopExperience.GetComponent(DesktopComponentKind.TimeDate);
    timeDate.Placement = new WindowPlacement { Left = 192, Top = 104, Width = 384, Height = 208 };
    timeDate.IsVisible = false;
    timeDate.Appearance.BackgroundColor = "#123456";

    var package = ThemeRules.Export(
        exportState, "测试主题", new ThemeArea(0, 0, 1920, 1040), new Version(0, 1, 0));

    using var stream = new MemoryStream();
    ThemeArchive.Write(stream, package);
    stream.Position = 0;

    var result = ThemeArchive.Read(stream, new Version(0, 1, 0));
    Assert(result.Succeeded, $"主题包读取应成功：{result.Error}");

    var experience = DesktopExperienceRules.CreateDefault();
    ThemeRules.ApplyVisuals(result.Package!, experience, new ThemeArea(100, 50, 3840, 2080));

    var restoredTimeDate = experience.GetComponent(DesktopComponentKind.TimeDate);
    Assert(NearlyEqual(restoredTimeDate.Placement.Left, 484), "TimeDate 左边距未按比例还原");
    Assert(NearlyEqual(restoredTimeDate.Placement.Top, 258), "TimeDate 上边距未按比例还原");
    Assert(NearlyEqual(restoredTimeDate.Placement.Width, 768), "TimeDate 宽度未按比例还原");
    Assert(NearlyEqual(restoredTimeDate.Placement.Height, 416), "TimeDate 高度未按比例还原");
    Assert(!restoredTimeDate.IsVisible, "组件可见性未随主题包往返保留");
    Assert(restoredTimeDate.Appearance.BackgroundColor == "#123456", "组件外观颜色未随主题包往返保留");
}

static bool NearlyEqual(double a, double b) => Math.Abs(a - b) < 0.01;

static void TestThemePackageRejectsUnsafeContent()
{
    var appVersion = new Version(0, 1, 0);
    var validManifest = BuildManifestJson(ThemeManifest.FormatName, 1, "0.1.0");

    using (var traversal = BuildZip(("../evil.json", "{}")))
    {
        var result = ThemeArchive.Read(traversal, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "路径穿越条目应被拒绝");
    }

    using (var scriptEntry = BuildZip(("manifest.json", validManifest), ("run.ps1", "Write-Host evil")))
    {
        var result = ThemeArchive.Read(scriptEntry, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "脚本文件条目应被拒绝");
    }

    using (var badFormat = BuildZip(("manifest.json", BuildManifestJson("other", 1, "0.1.0"))))
    {
        var result = ThemeArchive.Read(badFormat, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "非冰蓝主题格式应被拒绝");
    }

    using (var futureSchema = BuildZip(
        ("manifest.json", BuildManifestJson(ThemeManifest.FormatName, 99, "0.1.0"))))
    {
        var result = ThemeArchive.Read(futureSchema, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "未来版本的主题包应被拒绝");
    }

    using (var tooNewApp = BuildZip(
        ("manifest.json", BuildManifestJson(ThemeManifest.FormatName, 1, "9.0.0"))))
    {
        var result = ThemeArchive.Read(tooNewApp, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "要求更高应用版本的主题包应被拒绝");
    }

    using (var oversizedTokens = BuildZip(
        ("manifest.json", validManifest),
        ("tokens.json", new string('a', (int)ThemeArchive.MaximumEntryBytes + 1))))
    {
        var result = ThemeArchive.Read(oversizedTokens, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "超大条目应被拒绝");
    }

    using (var notAZip = new MemoryStream(Encoding.UTF8.GetBytes("not a zip file")))
    {
        var result = ThemeArchive.Read(notAZip, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "非法压缩包应被拒绝");
    }

    using (var manifestOnly = BuildZip(("manifest.json", validManifest)))
    {
        var result = ThemeArchive.Read(manifestOnly, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "缺少外观和布局的主题包应被拒绝");
    }

    using (var badVersion = BuildZip(
        ("manifest.json", BuildManifestJson(ThemeManifest.FormatName, 1, "not-a-version")),
        ("tokens.json", "{}"),
        ("layout.json", "{}")))
    {
        var result = ThemeArchive.Read(badVersion, appVersion);
        Assert(!result.Succeeded && result.Error.Length > 0, "无效的最低版本号应被拒绝");
    }

    var pathLike = new ThemePackage();
    pathLike.Bindings.Apps.Add(new ThemeAppBinding { Slot = "browser", DisplayName = @"C:\Users\someone\app" });
    ThemeRules.Normalize(pathLike);
    Assert(pathLike.Bindings.Apps.Single().DisplayName.Length == 0, "像路径的应用显示名不应进入主题包");
}

static string BuildManifestJson(string format, int schemaVersion, string minimumAppVersion) =>
    "{" +
    $"\"Format\":\"{format}\"," +
    $"\"SchemaVersion\":{schemaVersion}," +
    "\"Name\":\"测试主题\"," +
    $"\"MinimumAppVersion\":\"{minimumAppVersion}\"" +
    "}";

static MemoryStream BuildZip(params (string Name, string Content)[] entries)
{
    var stream = new MemoryStream();
    using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
    {
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            using var entryStream = entry.Open();
            var bytes = Encoding.UTF8.GetBytes(content);
            entryStream.Write(bytes, 0, bytes.Length);
        }
    }
    stream.Position = 0;
    return stream;
}

static void TestThemeValueNormalization()
{
    var package = new ThemePackage();
    package.Tokens.Components.Add(new ThemeComponentTokens
    {
        Kind = DesktopComponentKind.TimeDate,
        FontScale = 100,
        CornerRadius = -5,
        Appearance = new WidgetAppearanceState { BackgroundOpacity = 5 }
    });
    package.Layout.Components.Add(new ThemeComponentPlacement
    {
        Kind = DesktopComponentKind.TimeDate,
        X = -1,
        Y = 2,
        Width = 5,
        Height = 0
    });
    package.Bindings.Apps.Add(new ThemeAppBinding { ExecutableName = @"C:\evil\x.exe" });
    package.Bindings.Apps.Add(new ThemeAppBinding { ExecutableName = "app.exe", AppUserModelId = @"a\b" });

    ThemeRules.Normalize(package);

    var tokens = package.Tokens.Components.Single();
    Assert(
        tokens.FontScale is >= DesktopExperienceRules.MinimumFontScale and <= DesktopExperienceRules.MaximumFontScale,
        "字号缩放未钳制到合法范围");
    Assert(
        tokens.CornerRadius is >= WidgetAppearanceRules.MinimumCornerRadius and <= WidgetAppearanceRules.MaximumCornerRadius,
        "圆角未钳制到合法范围");
    Assert(
        tokens.Appearance.BackgroundOpacity is >= WidgetAppearanceRules.MinimumBackgroundOpacity and <= 1d,
        "主体透明度未钳制到合法范围");

    var placement = package.Layout.Components.Single();
    Assert(placement.X is >= 0d and <= 1d, "布局 X 未钳制到 0..1");
    Assert(placement.Y is >= 0d and <= 1d, "布局 Y 未钳制到 0..1");
    Assert(placement.Width is > 0d and <= 1d, "布局宽度未钳制到 0..1");
    Assert(placement.Height is > 0d and <= 1d, "布局高度未钳制到 0..1");
    Assert(placement.X + placement.Width <= 1d + 1e-9, "布局 X+宽度超出工作区");
    Assert(placement.Y + placement.Height <= 1d + 1e-9, "布局 Y+高度超出工作区");

    var pathLikeBinding = package.Bindings.Apps[0];
    var normalBinding = package.Bindings.Apps[1];
    Assert(pathLikeBinding.ExecutableName is null, "路径式可执行文件名应被丢弃");
    Assert(normalBinding.ExecutableName == "app.exe", "合法可执行文件名应保留");
    Assert(normalBinding.AppUserModelId is null, "包含反斜杠的应用模型 ID 应被丢弃");
}

static void TestThemeSlotBindingResolution()
{
    var bindings = new ThemeBindings
    {
        Apps =
        [
            new ThemeAppBinding { Slot = "browser", ExecutableName = "chrome.exe" },
            new ThemeAppBinding { Slot = "chat", ExecutableName = "QQ.exe" }
        ]
    };

    var result = ThemeRules.ResolveBindings(bindings, (_, executableName) =>
        executableName == "msedge.exe"
            ? new DockPinnedApp
            {
                DisplayName = "Edge",
                ExecutablePath = @"C:\Program Files\Microsoft\Edge\msedge.exe"
            }
            : null);

    Assert(result.Resolved.Count == 1, "应只解析出一个可用应用");
    Assert(
        result.Resolved[0].ExecutablePath!.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase),
        "解析出的应用应为 msedge");
    Assert(
        result.Missing.Count == 1 && result.Missing[0].Slot == "chat",
        "未匹配的聊天槽位应记为缺失");

    Assert(AppSlotCatalog.SlotFor("CHROME.EXE") == "browser", "浏览器可执行文件槽位识别错误");
    Assert(AppSlotCatalog.SlotFor("unknown.exe") == "other", "未知可执行文件应归为其他槽位");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void TestRemoveBlank()
{
    var widget = new TodoWidgetState();
    var kept = TodoService.Add(widget, "有内容");
    TodoService.Add(widget, "");
    TodoService.Add(widget, "   ");
    var typing = TodoService.Add(widget, "", DateTime.Today.AddDays(-1));
    var done = TodoService.Add(widget, "已完成");
    done.IsCompleted = true;

    var removed = TodoService.RemoveBlank(widget, x => x.Id == typing.Id);
    Assert(removed.Count == 2 && removed.All(x => string.IsNullOrWhiteSpace(x.Text)),
        "应删除空串与纯空白两条待办");
    Assert(widget.Items.Select(x => x.Id).SequenceEqual([kept.Id, typing.Id, done.Id]),
        "有内容、已完成与正在输入的条目应保留且相对顺序不变");
    Assert(widget.Items.Select(x => x.Order).SequenceEqual([0, 1, 2]), "删除后 Order 应重新连续编号");

    removed = TodoService.RemoveBlank(widget);
    Assert(removed.Single().Id == typing.Id && widget.Items.Count == 2, "结束输入后空白条目也应删除");
    Assert(TodoService.RemoveBlank(widget).Count == 0, "没有空白条目时不应删除");
}
