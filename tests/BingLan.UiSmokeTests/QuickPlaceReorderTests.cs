using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using BingLan.App.Windows;
using BingLan.Core.Models;
using BingLan.Core.Services;

internal static class QuickPlaceReorderTests
{
    internal static void Run()
    {
        List<QuickPlaceState> saved =
        [
            new() { Icon = "folder", Name = "同名", Target = @"C:\First" },
            new() { Icon = "folder", Name = "中间", Target = @"C:\Middle" },
            new() { Icon = "folder", Name = "同名", Target = @"C:\Last" }
        ];
        var writes = 0;
        var reads = 0;
        var settings = CreateSettings(new SettingsMaintenance
        {
            QuickPlaces = () => { reads++; return saved; },
            SetQuickPlaces = items => { saved = items; writes++; }
        });
        try
        {
            settings.ShowComponentSettings(DesktopComponentKind.QuickLaunch);
            var list = (ListBox)settings.FindName("QuickPlaceList");
            Check(list.AllowDrop && !settings.IsVisible, "快捷入口列表应支持拖放，检查无需显示窗口");

            var drop = Drop(list, DragData(settings, 0));
            Check(drop.Handled && drop.Effects == DragDropEffects.Move && writes == 1,
                "放到列表末尾应通过绑定的 Drop 处理器保存一次");
            Check(saved.Select(item => item.Target).SequenceEqual([@"C:\Middle", @"C:\Last", @"C:\First"]),
                "跨多项拖放应保留中间项顺序，且同名入口按身份移动");
            Check(list.SelectedIndex == 2 && ((TextBox)settings.FindName("QuickPlaceTargetEditor")).Text == @"C:\First",
                "移动后应继续选中同一入口并显示其编辑内容");

            var editor = (TextBox)settings.FindName("QuickPlaceNameEditor");
            editor.Text = "拖动后编辑";
            editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
            Check(saved[2].Name == "拖动后编辑" && saved[1].Name == "同名", "拖动后的编辑应保存到移动的入口");

            var buttons = ((StackPanel)list.Parent).Children.OfType<WrapPanel>().Single().Children.OfType<Button>();
            buttons.Single(button => Equals(button.Content, "上移")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(list.SelectedIndex == 1 && saved[1].Target == @"C:\First", "原有上移按钮应继续移动并选中入口");
            buttons.Single(button => Equals(button.Content, "下移")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(list.SelectedIndex == 2 && saved[2].Target == @"C:\First", "原有下移按钮应继续移动并选中入口");

            var priorWrites = writes;
            Check(!settings.ReorderQuickPlace(2, 2) && !settings.ReorderQuickPlace(-1, 0)
                && !settings.ReorderQuickPlace(0, list.Items.Count) && writes == priorWrites,
                "原位置与失效索引不得改写状态");
            Check(Drop(list, new DataObject(DataFormats.FileDrop, new[] { @"C:\External" })).Effects == DragDropEffects.None
                && writes == priorWrites, "外部文件拖入不得改变快捷入口顺序");

            var readsBeforeReopen = reads;
            settings.ShowComponentSettings(DesktopComponentKind.QuickLaunch);
            var targetEditor = (TextBox)settings.FindName("QuickPlaceTargetEditor");
            Check(reads > readsBeforeReopen && list.SelectedIndex == 0 && targetEditor.Text == @"C:\Middle",
                $"重新打开详情应读取保存后的顺序（读取 {readsBeforeReopen}→{reads}，选中索引 {list.SelectedIndex}，位置 {targetEditor.Text}）");
        }
        finally
        {
            settings.Close();
        }

        TestSelectedScreen();
    }

    private static void TestSelectedScreen()
    {
        const string screen = "DISPLAY#QUICK_PLACE_TEST#2";
        string? savedScreen = null;
        List<QuickPlaceState>? saved = null;
        var rootWrites = 0;
        var settings = CreateSettings(new SettingsMaintenance
        {
            SetQuickPlaces = _ => rootWrites++,
            MultiScreen = new MultiScreenSettingsBridge
            {
                IsEnabled = () => true,
                GetScreens = () => [new(screen, screen, "第二屏", "第二屏", "1920×1080", 2, false, true,
                    new(1920, 0, 3840, 1080), new(1920, 0, 3840, 1040), 96, 0)],
                GetQuickPlaces = _ => saved
                    ?? [new() { Name = "甲", Target = @"C:\A" }, new() { Name = "乙", Target = @"C:\B" }],
                SetQuickPlaces = (key, items) => { savedScreen = key; saved = items; }
            }
        });
        try
        {
            settings.SelectActiveScreen(screen);
            settings.ShowComponentSettings(DesktopComponentKind.QuickLaunch);
            var list = (ListBox)settings.FindName("QuickPlaceList");
            Drop(list, DragData(settings, 0));
            Check(savedScreen == screen && rootWrites == 0 && saved is { Count: 2 } && saved[1].Target == @"C:\A",
                "拖动应只保存到当前选中屏的快捷入口");
            settings.ShowComponentSettings(DesktopComponentKind.QuickLaunch);
            Check(list.SelectedIndex == 0 && ((TextBox)settings.FindName("QuickPlaceTargetEditor")).Text == @"C:\B",
                "重开详情应显示当前屏幕保存的快捷入口顺序");
        }
        finally
        {
            settings.Close();
        }
    }

    private static SettingsWindow CreateSettings(SettingsMaintenance maintenance) =>
        new(new CityLookupService(), new InformationWidgetState(), DesktopExperienceRules.CreateDefault(),
            (_, _) => { }, _ => { }, _ => { }, maintenance: maintenance);

    private static DataObject DragData(SettingsWindow settings, int index)
    {
        var type = typeof(SettingsWindow).GetNestedType("QuickPlaceDrag", BindingFlags.NonPublic)!;
        var items = (List<QuickPlaceState>)typeof(SettingsWindow).GetField("_quickPlaces", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(settings)!;
        return new DataObject(type, Activator.CreateInstance(type, settings, items[index])!);
    }

    private static DragEventArgs Drop(ListBox list, IDataObject data)
    {
        var args = (DragEventArgs?)Activator.CreateInstance(typeof(DragEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            args: [data, DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, list, new Point()],
            culture: null) ?? throw new InvalidOperationException("无法创建 WPF 拖放事件");
        args.RoutedEvent = DragDrop.DropEvent;
        list.RaiseEvent(args);
        return args;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
