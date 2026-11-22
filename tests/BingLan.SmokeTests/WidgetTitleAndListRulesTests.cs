using System.Text.Json;
using BingLan.Core.Models;
using BingLan.Core.Services;

namespace BingLan.SmokeTests;

internal static class WidgetTitleAndListRulesTests
{
    public static void Run()
    {
        // 集中维护的默认名称常量与回退规则
        Check(WidgetTitleRules.DefaultFileBoxTitle == "文件收纳盒", "文件收纳盒默认名常量应为“文件收纳盒”");
        Check(WidgetTitleRules.LegacyDefaultFileBoxTitle == "桌面分组", "应保留旧默认名常量用于兼容判断");
        Check(WidgetTitleRules.DefaultTodoTitle == "今日待办", "待办默认名常量应为“今日待办”");
        Check(WidgetTitleRules.TodoEmptyTitleFallback == "待办便签", "待办空白回落名应保持“待办便签”");
        Check(WidgetTitleRules.DefaultNoteTitle == "便签", "便签默认名常量应为“便签”");

        Check(new FileBoxState().Title == WidgetTitleRules.DefaultFileBoxTitle, "新建 FileBoxState 默认名应为“文件收纳盒”");
        Check(!new FileBoxState().IsHidden, "新建 FileBoxState 默认应可见");
        Check(new NoteWidgetState().Title == WidgetTitleRules.DefaultNoteTitle, "新建 NoteWidgetState 默认名应为“便签”");
        Check(!new NoteWidgetState().IsHidden, "新建 NoteWidgetState 默认应可见");
        Check(new TodoWidgetState().Title == WidgetTitleRules.DefaultTodoTitle, "新建 TodoWidgetState 默认名应为“今日待办”");

        // 新建文件收纳盒不重名递增规则
        Check(WidgetTitleRules.NextTitle(EditableWidgetTitleKind.FileBox, []) == "文件收纳盒",
            "首个文件收纳盒应为“文件收纳盒”");
        Check(WidgetTitleRules.NextTitle(EditableWidgetTitleKind.FileBox, ["文件收纳盒"]) == "文件收纳盒 2",
            "第二个文件收纳盒应递增为“文件收纳盒 2”");
        Check(WidgetTitleRules.NextTitle(EditableWidgetTitleKind.FileBox, ["文件收纳盒", "文件收纳盒 2"]) == "文件收纳盒 3",
            "第三个文件收纳盒应递增为“文件收纳盒 3”");
        Check(WidgetTitleRules.NextTitle(EditableWidgetTitleKind.FileBox, ["桌面分组", "工作"]) == "文件收纳盒",
            "已有旧名称“桌面分组”不应阻止新建盒子采用“文件收纳盒”");

        // 名称提交与空白回落规则（含零宽字符与 Unicode 控制字符清理）
        Check(WidgetTitleRules.CommitTitle("  项目文档  ", EditableWidgetTitleKind.FileBox) == "项目文档",
            "提交自定义名称应去除首尾空白");
        Check(WidgetTitleRules.CommitTitle("   ", EditableWidgetTitleKind.FileBox) == "文件收纳盒",
            "文件收纳盒空白名称应回落到“文件收纳盒”");
        Check(WidgetTitleRules.CommitTitle(null, EditableWidgetTitleKind.Note) == "便签",
            "便签空白名称应回落到“便签”");
        Check(WidgetTitleRules.CommitTitle("", EditableWidgetTitleKind.Todo) == "待办便签",
            "待办空白名称应回落到“待办便签”");
        Check(WidgetTitleRules.CommitTitle("\u200B\u200C\u200D\uFEFF\u2060", EditableWidgetTitleKind.FileBox) == "文件收纳盒",
            "仅包含零宽字符的名称清理后应按空白回落");
        Check(WidgetTitleRules.CommitTitle(" \u0000\u0007\t\r\n\u001F\u007F\u009F ", EditableWidgetTitleKind.Note) == "便签",
            "仅包含控制字符与空白的名称清理后应按空白回落");
        Check(WidgetTitleRules.CommitTitle("\uFEFF  工作\u200B清单\u0007\u2060 ", EditableWidgetTitleKind.Todo) == "工作清单",
            "名称中的零宽字符与控制字符应被去除并保留正常文字");

        // 改名与空白回落重名消重规则（排除自身，重名自动追加 " 2"、" 3"）
        Check(WidgetTitleRules.CommitUniqueTitle("工作", EditableWidgetTitleKind.FileBox, ["学习"]) == "工作",
            "未与其他同类型卡片重名时应保持提交名称");
        Check(WidgetTitleRules.CommitUniqueTitle("工作", EditableWidgetTitleKind.FileBox, ["工作"]) == "工作 2",
            "改名与其他卡片重名时应自动追加“ 2”");
        Check(WidgetTitleRules.CommitUniqueTitle("工作", EditableWidgetTitleKind.FileBox, ["工作", "工作 2"]) == "工作 3",
            "“工作”与“工作 2”均被占用时应自动追加“ 3”");
        Check(WidgetTitleRules.CommitUniqueTitle("WORK", EditableWidgetTitleKind.Note, ["work"]) == "WORK 2",
            "重名检测应忽略大小写");
        Check(WidgetTitleRules.CommitUniqueTitle("文件收纳盒 2", EditableWidgetTitleKind.FileBox, ["文件收纳盒"]) == "文件收纳盒 2",
            "排除自身后，保留原名“文件收纳盒 2”不应被重复追加编号");
        Check(WidgetTitleRules.CommitUniqueTitle("   ", EditableWidgetTitleKind.FileBox, ["文件收纳盒"]) == "文件收纳盒 2",
            "空白回落与同类型其他卡片重名时应自动递增为“文件收纳盒 2”");
        Check(WidgetTitleRules.CommitUniqueTitle("\u200B\uFEFF", EditableWidgetTitleKind.FileBox, ["文件收纳盒", "文件收纳盒 2"]) == "文件收纳盒 3",
            "零宽字符回落与已有卡片重名时应递增为“文件收纳盒 3”");
        Check(WidgetTitleRules.CommitUniqueTitle("", EditableWidgetTitleKind.Todo, ["待办便签"]) == "待办便签 2",
            "待办空白回落与其他待办重名时应递增为“待办便签 2”");
        var noteForUnique = new NoteWidgetState { Title = "旧便签" };
        NoteService.CommitTitle(noteForUnique, "\u200B  ", ["便签"]);
        Check(noteForUnique.Title == "便签 2",
            "NoteService.CommitTitle 应同步执行零宽清理、空白回落与重名递增");

        // 文件收纳盒贴边隐藏色条提示文字格式化
        Check(FileBoxEdgeDockRules.FormatTabToolTip("项目资料") == "项目资料（移入展开）",
            "贴边隐藏色条提示文字应反映最新盒名");
        Check(FileBoxEdgeDockRules.FormatTabToolTip("  \u200B  ") == "文件收纳盒（移入展开）",
            "贴边隐藏色条提示文字在标题为空白或零宽时应回落默认名");

        // 旧默认名兼容识别且不改写旧数据
        Check(WidgetTitleRules.IsDefaultFileBoxTitle("文件收纳盒"), "应识别当前默认文件收纳盒名称");
        Check(WidgetTitleRules.IsDefaultFileBoxTitle("桌面分组"), "应兼容识别旧默认文件收纳盒名称“桌面分组”");
        Check(!WidgetTitleRules.IsDefaultFileBoxTitle("文件收纳盒 2")
                && !WidgetTitleRules.IsDefaultFileBoxTitle("我的工作"),
            "自定义或已编号名称不应误判为未修改默认名");

        var legacyJson = """{"Title":"桌面分组","Items":[]}""";
        var restoredLegacyBox = JsonSerializer.Deserialize<FileBoxState>(legacyJson);
        Check(restoredLegacyBox is { Title: "桌面分组", IsHidden: false },
            "反序列化旧数据中标题为“桌面分组”的文件收纳盒必须保留原名称且默认显示");

        // 列表溢出计数与文案规则
        Check(ComponentListScrollRules.ComputeHiddenBelowCount(8, 0, 10, 8, canContentScroll: true) == 0,
            "条目全部放得下时下方隐藏数应为 0");
        Check(ComponentListScrollRules.ComputeHiddenBelowCount(15, 0, 9, 15, canContentScroll: true) == 6,
            "项滚动模式下顶部应精确算出下方还有 6 项");
        Check(ComponentListScrollRules.ComputeHiddenBelowCount(15, 6, 9, 15, canContentScroll: true) == 0,
            "滚到底部后下方隐藏数应归零");
        Check(ComponentListScrollRules.ComputeHiddenBelowCount(15, 2, 9, 15, canContentScroll: true) == 4,
            "向下滚动 2 项后下方隐藏数应减少到 4 项");
        Check(ComponentListScrollRules.ComputeHiddenBelowCount(10, 0, 250, 500, canContentScroll: false) == 5,
            "像素滚动模式下应按平均项高推算下方隐藏数");
        Check(ComponentListScrollRules.ComputeHiddenBelowCount(10, 250, 250, 500, canContentScroll: false) == 0,
            "像素滚动模式下滚到底部应归零");
        Check(ComponentListScrollRules.ComputeHiddenBelowCount(0, 0, 0, 0, canContentScroll: true) == 0,
            "空列表或零视口应安全返回 0");
        Check(ComponentListScrollRules.FormatOverflowHint(4) == "下方还有 4 项",
            "溢出提示文案应准确显示剩余项数");
        Check(ComponentListScrollRules.FormatOverflowHint(0) == string.Empty,
            "无溢出时提示文案应为空");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
