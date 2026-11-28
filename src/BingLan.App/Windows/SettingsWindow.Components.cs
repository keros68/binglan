using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BingLan.Core.Models;
using BingLan.Core.Services;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using TextBox = System.Windows.Controls.TextBox;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private readonly HashSet<WidgetWindowBase> _observedTitleWindows = [];
    private ScrollViewer? _componentListScrollViewer;
    private bool _loadingTitleEditor;
    private bool _committingTitleEditor;
    private (TodoWidgetWindow Window, string OriginalTitle, string Draft)? _todoTitleEdit;

    // The WPF TextBox binding defers source updates during IME composition.
    public string TodoTitleDraft
    {
        get => _todoTitleEdit?.Draft ?? string.Empty;
        set
        {
            if (_loadingTitleEditor || _committingTitleEditor || _isClosed
                || _selectedComponentEntry is not { Kind: DesktopComponentKind.Todo, Window: TodoWidgetWindow todo })
            {
                return;
            }

            var originalTitle = todo.State.Title;
            if (_todoTitleEdit is { } edit && edit.Window == todo)
            {
                originalTitle = edit.OriginalTitle;
            }
            else if (string.Equals(value, originalTitle, StringComparison.Ordinal))
            {
                return;
            }

            _todoTitleEdit = (todo, originalTitle, value);
            if (WidgetTitleRules.CommitTitle(value, string.Empty).Length > 0)
            {
                ApplyTitleEditorForWindow(todo, SelectedComponentTitleEditor, value, normalizeEditor: false);
            }
        }
    }

    private void InitializeComponentsPageEnhancements()
    {
        DesktopComponentList.Loaded += (_, _) =>
        {
            HookComponentListScrollViewer();
            ScheduleComponentListOverflowUpdate();
            if (DesktopComponentList.SelectedItem is DesktopComponentSettingsEntry selected)
            {
                ScrollSelectedComponentIntoView(selected);
            }
        };
        DesktopComponentList.SizeChanged += (_, _) => ScheduleComponentListOverflowUpdate();
    }

    private FileBoxWindow? _settingsRevealedFileBox;

    private void UpdateSettingsRevealedFileBox(FileBoxWindow? target)
    {
        if (ReferenceEquals(_settingsRevealedFileBox, target))
        {
            return;
        }
        _settingsRevealedFileBox?.SetSettingsRevealed(false);
        _settingsRevealedFileBox = target;
        _settingsRevealedFileBox?.SetSettingsRevealed(true);
    }

    internal void RefreshWidgetsAfterGroupChange(FileBoxWindow? preferred)
    {
        if (_isClosed)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => RefreshWidgetsAfterGroupChange(preferred));
            return;
        }
        RefreshComponentList(
            preferredWindow: preferred ?? _selectedComponentEntry?.Window,
            preferredKind: preferred is null ? _selectedComponentEntry?.Kind : null);
        if (IsMultiScreenActive)
        {
            RefreshMultiScreenTopologyBarsOnly();
        }
    }

    private void OnObservedFileBoxActiveTabSwitching(FileBoxWindow box)
    {
        if (!_isClosed && ReferenceEquals(_selectedComponentEntry?.Window, box)
            && !string.Equals(SelectedWidgetTitleEditor.Text, box.State.Title, StringComparison.Ordinal))
        {
            CommitPendingTitleEdits();
        }
    }

    private void OnObservedFileBoxActiveTabChanged(FileBoxWindow box)
    {
        if (_isClosed || _loadingViewMode)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnObservedFileBoxActiveTabChanged(box));
            return;
        }

        UpdateComponentEntryNameInList(box, GetWidgetListDisplayName(box));
        if (ReferenceEquals(_selectedComponentEntry?.Window, box))
        {
            LoadFileBoxViewMode(box);
            LoadFileBoxCollect(box);
            UpdateSelectedWidgetHeaderBarState(_selectedComponentEntry);
        }
    }

    private void UnsubscribeObservedWidgetTitles()
    {
        UpdateSettingsRevealedFileBox(null);
        foreach (var window in _observedTitleWindows)
        {
            window.TitleChanged -= OnObservedWidgetTitleChanged;
            if (window is FileBoxWindow box)
            {
                box.ActiveTabSwitching -= OnObservedFileBoxActiveTabSwitching;
                box.ActiveTabChanged -= OnObservedFileBoxActiveTabChanged;
                box.WidgetChanged -= OnObservedFileBoxStateChanged;
                box.EdgeDockStateChanged -= OnObservedFileBoxEdgeDockToggled;
            }
        }
        _observedTitleWindows.Clear();
    }

    private void OnObservedFileBoxStateChanged(WidgetWindowBase window)
    {
        if (_isClosed || _loadingViewMode || window is not FileBoxWindow box)
        {
            return;
        }
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnObservedFileBoxStateChanged(window));
            return;
        }
        if (ReferenceEquals(_selectedComponentEntry?.Window, box))
        {
            LoadFileBoxViewMode(box);
        }
    }

    private void OnObservedFileBoxEdgeDockToggled(FileBoxWindow box, bool _)
    {
        if (_isClosed)
        {
            return;
        }
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isClosed && ReferenceEquals(_selectedComponentEntry?.Window, box))
            {
                LoadFileBoxViewMode(box);
            }
        });
    }

    private void SyncObservedWidgetTitles(IEnumerable<WidgetWindowBase> windows)
    {
        var current = windows
            .Where(window => window is TodoWidgetWindow or NoteWidgetWindow or FileBoxWindow)
            .ToHashSet();

        foreach (var stale in _observedTitleWindows.Where(window => !current.Contains(window)).ToList())
        {
            stale.TitleChanged -= OnObservedWidgetTitleChanged;
            if (stale is FileBoxWindow staleBox)
            {
                staleBox.ActiveTabSwitching -= OnObservedFileBoxActiveTabSwitching;
                staleBox.ActiveTabChanged -= OnObservedFileBoxActiveTabChanged;
                staleBox.WidgetChanged -= OnObservedFileBoxStateChanged;
                staleBox.EdgeDockStateChanged -= OnObservedFileBoxEdgeDockToggled;
            }
            _observedTitleWindows.Remove(stale);
        }

        foreach (var added in current)
        {
            if (_observedTitleWindows.Add(added))
            {
                added.TitleChanged += OnObservedWidgetTitleChanged;
                if (added is FileBoxWindow addedBox)
                {
                    addedBox.ActiveTabSwitching += OnObservedFileBoxActiveTabSwitching;
                    addedBox.ActiveTabChanged += OnObservedFileBoxActiveTabChanged;
                    addedBox.WidgetChanged += OnObservedFileBoxStateChanged;
                    addedBox.EdgeDockStateChanged += OnObservedFileBoxEdgeDockToggled;
                }
            }
        }
    }

    private void OnObservedWidgetTitleChanged(WidgetWindowBase window)
    {
        if (_isClosed || _committingTitleEditor)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnObservedWidgetTitleChanged(window));
            return;
        }

        var updatedName = GetWidgetName(window);
        UpdateComponentEntryNameInList(window, GetWidgetListDisplayName(window));

        if (ReferenceEquals(_selectedComponentEntry?.Window, window))
        {
            if (window is TodoWidgetWindow)
            {
                SelectedComponentHeadingText.Text = updatedName;
                ResetTodoTitleEditor(updatedName);
                return;
            }

            SelectedWidgetHeadingText.Text = updatedName;
            if (!SelectedWidgetTitleEditor.IsKeyboardFocusWithin)
            {
                _loadingTitleEditor = true;
                try
                {
                    SelectedWidgetTitleEditor.Text = updatedName;
                }
                finally
                {
                    _loadingTitleEditor = false;
                }
            }
        }
    }

    private void UpdateComponentEntryNameInList(WidgetWindowBase window, string updatedName)
    {
        if (DesktopComponentList?.ItemsSource is not List<DesktopComponentSettingsEntry> entries)
        {
            return;
        }

        if (entries.Find(item => ReferenceEquals(item.Window, window)) is { } entry)
        {
            entry.Name = updatedName;
        }
    }

    internal static bool CanRenameEntry(DesktopComponentSettingsEntry? entry) =>
        entry?.Window is TodoWidgetWindow or NoteWidgetWindow or FileBoxWindow;

    private IEnumerable<string> CollectOtherSameKindTitles(WidgetWindowBase target) => target switch
    {
        TodoWidgetWindow => _widgetProvider()
            .OfType<TodoWidgetWindow>()
            .Where(other => !ReferenceEquals(other, target))
            .Select(other => other.State.Title),
        NoteWidgetWindow => _widgetProvider()
            .OfType<NoteWidgetWindow>()
            .Where(other => !ReferenceEquals(other, target))
            .Select(other => other.State.Title),
        FileBoxWindow fileBox => _widgetProvider()
            .OfType<FileBoxWindow>()
            .SelectMany(other => other.Tabs)
            .Where(tab => tab.Id != fileBox.State.Id)
            .Select(tab => tab.Title),
        _ => []
    };

    private static bool IsWidgetShown(WidgetWindowBase window) => window switch
    {
        FileBoxWindow box => !box.State.IsHidden && box.IsVisible,
        NoteWidgetWindow note => !note.State.IsHidden && note.IsVisible,
        _ => window.IsVisible
    };

    private void UpdateBuiltInHeaderBarState(
        DesktopComponentKind kind,
        DesktopComponentSettingsEntry entry)
    {
        var component = ActiveDesktopExperience.GetComponent(kind);
        var isTodo = kind == DesktopComponentKind.Todo;
        var canRename = isTodo && entry.Window is TodoWidgetWindow;

        SelectedComponentCaptionText.Text = kind switch
        {
            DesktopComponentKind.Todo when canRename => "待办卡片 · 未锁定时可直接拖动空白区，改名立即同步到桌面卡片。",
            DesktopComponentKind.AudioVisualizer => "安静时显示一排短线。默认扬声器或耳机播放声音时，柱子从左到右显示低音到高音。",
            _ => "未锁定时可直接拖动桌面空白区。"
        };

        BuiltInComponentTitleRow.Visibility = canRename ? Visibility.Visible : Visibility.Collapsed;
        ResetTodoTitleEditor(canRename ? GetWidgetName(entry.Window!) : string.Empty);

        if (isTodo)
        {
            DeleteComponentButton.Content = "删除此卡片";
            AutomationProperties.SetName(DeleteComponentButton, "删除此卡片");
            DeleteComponentButton.IsEnabled = entry.Window is not null;
        }
        else
        {
            DeleteComponentButton.Content = "从桌面移除";
            AutomationProperties.SetName(DeleteComponentButton, "从桌面移除");
            DeleteComponentButton.IsEnabled = entry.Window is not null && component.IsVisible;
        }
    }

    private void UpdateSelectedWidgetHeaderBarState(DesktopComponentSettingsEntry entry)
    {
        if (entry.Window is not { } window)
        {
            return;
        }

        var name = GetWidgetName(window);
        SelectedWidgetHeadingText.Text = name;
        SelectedWidgetCaptionText.Text = window switch
        {
            FileBoxWindow => "文件收纳盒 · 未锁定时可直接拖动空白区，删除只删映射不删原文件。",
            NoteWidgetWindow => "普通便签 · 未锁定时可直接拖动标题区，改名立即同步到桌面卡片。",
            _ => "未锁定时可直接拖动空白区；修改名称后立即同步到桌面卡片。"
        };
        _loadingTitleEditor = true;
        try
        {
            SelectedWidgetTitleEditor.Text = name;
        }
        finally
        {
            _loadingTitleEditor = false;
        }
        SelectedWidgetVisibleCheckBox.IsChecked = IsWidgetShown(window);
        SelectedWidgetLockedCheckBox.IsChecked = window.IsWidgetLocked;
    }

    private void CommitPendingTitleEdits()
    {
        if (_loadingTitleEditor || _committingTitleEditor || _selectedComponentEntry?.Window is not { } window)
        {
            return;
        }

        if (window is TodoWidgetWindow todo
            && BuiltInComponentTitleRow.Visibility == Visibility.Visible)
        {
            FinishTodoTitleEdit(todo);
        }
        else if (_selectedComponentEntry.Kind is null
            && SelectedWidgetHeaderBar.Visibility == Visibility.Visible)
        {
            ApplyTitleEditorForWindow(window, SelectedWidgetTitleEditor, SelectedWidgetTitleEditor.Text);
        }
    }

    private void FinishTodoTitleEdit(TodoWidgetWindow todo, bool cancel = false)
    {
        if (_todoTitleEdit is not { } edit || edit.Window != todo)
        {
            return;
        }

        _todoTitleEdit = null;
        if (cancel && string.Equals(todo.State.Title, edit.OriginalTitle, StringComparison.Ordinal))
        {
            ResetTodoTitleEditor(todo.State.Title);
        }
        else if (cancel || WidgetTitleRules.CommitTitle(edit.Draft, string.Empty).Length == 0)
        {
            ApplyTitleEditorForWindow(todo, SelectedComponentTitleEditor, cancel ? edit.OriginalTitle : string.Empty);
        }
        else
        {
            ResetTodoTitleEditor(todo.State.Title);
        }
    }

    private void ResetTodoTitleEditor(string title)
    {
        _todoTitleEdit = null;
        _loadingTitleEditor = true;
        try
        {
            SelectedComponentTitleEditor.SetCurrentValue(TextBox.TextProperty, title);
        }
        finally
        {
            _loadingTitleEditor = false;
        }
    }

    private void ApplyTitleEditorForWindow(
        WidgetWindowBase window,
        TextBox editor,
        string title,
        bool normalizeEditor = true)
    {
        if (_loadingTitleEditor || _committingTitleEditor)
        {
            return;
        }

        var previousName = GetWidgetName(window);
        var otherTitles = window.SameKindOtherTitles?.Invoke() ?? CollectOtherSameKindTitles(window);
        _committingTitleEditor = true;
        try
        {
            var changed = window.ApplyWidgetTitle(title, otherTitles);
            var committed = GetWidgetName(window);
            _loadingTitleEditor = true;
            try
            {
                if (normalizeEditor)
                {
                    editor.SetCurrentValue(TextBox.TextProperty, committed);
                }
                if (window is TodoWidgetWindow)
                {
                    SelectedComponentHeadingText.Text = committed;
                }
                else
                {
                    SelectedWidgetHeadingText.Text = committed;
                }
            }
            finally
            {
                _loadingTitleEditor = false;
            }

            UpdateComponentEntryNameInList(window, GetWidgetListDisplayName(window));
            if (window is FileBoxWindow renamedBox && ReferenceEquals(_selectedComponentEntry?.Window, renamedBox))
            {
                LoadFileBoxViewMode(renamedBox);
            }
            if (changed && !string.Equals(previousName, committed, StringComparison.Ordinal))
            {
                ComponentSettingsStatusText.Text = $"已重命名为“{committed}”";
            }
        }
        finally
        {
            _committingTitleEditor = false;
        }
    }

    private void ComponentTitleEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry is { Kind: DesktopComponentKind.Todo, Window: TodoWidgetWindow todo })
        {
            FinishTodoTitleEdit(todo);
        }
    }

    private void ComponentTitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (_selectedComponentEntry is not { Kind: DesktopComponentKind.Todo, Window: TodoWidgetWindow todo })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            FinishTodoTitleEdit(todo);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            FinishTodoTitleEdit(todo, cancel: true);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void WidgetTitleEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry is { Kind: null, Window: { } window })
        {
            ApplyTitleEditorForWindow(window, SelectedWidgetTitleEditor, SelectedWidgetTitleEditor.Text);
        }
    }

    private void WidgetTitleEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (_selectedComponentEntry is not { Kind: null, Window: { } window })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            ApplyTitleEditorForWindow(window, SelectedWidgetTitleEditor, SelectedWidgetTitleEditor.Text);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _loadingTitleEditor = true;
            try
            {
                SelectedWidgetTitleEditor.Text = GetWidgetName(window);
            }
            finally
            {
                _loadingTitleEditor = false;
            }
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void SelectedWidgetVisibleCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedComponentEntry?.Window is not { } window)
        {
            return;
        }

        var visible = SelectedWidgetVisibleCheckBox.IsChecked == true;
        if (_maintenance.SetWidgetVisibility is { } setVisibility)
        {
            setVisibility(window, visible);
        }
        else
        {
            switch (window)
            {
                case FileBoxWindow box:
                    box.State.IsHidden = !visible;
                    break;
                case NoteWidgetWindow note:
                    note.State.IsHidden = !visible;
                    break;
            }
            if (visible)
            {
                window.Show();
            }
            else
            {
                window.HideFromApp();
            }
        }

        ComponentSettingsStatusText.Text = visible
            ? $"已显示“{GetWidgetName(window)}”"
            : $"已隐藏“{GetWidgetName(window)}”";
        RefreshComponentList(window);
    }

    private void ComponentListItem_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: DesktopComponentSettingsEntry entry } item)
        {
            e.Handled = true;
            return;
        }

        var canRename = CanRenameEntry(entry);
        if (item.ContextMenu is { } menu)
        {
            foreach (var menuItem in menu.Items.OfType<MenuItem>())
            {
                menuItem.IsEnabled = canRename;
            }
        }

        if (!canRename)
        {
            e.Handled = true;
        }
    }

    private void ComponentListRenameMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DesktopComponentSettingsEntry? target = null;
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: FrameworkElement { DataContext: DesktopComponentSettingsEntry clicked } } })
        {
            target = clicked;
        }
        target ??= _selectedComponentEntry;

        if (!CanRenameEntry(target))
        {
            return;
        }

        if (!ReferenceEquals(DesktopComponentList.SelectedItem, target))
        {
            DesktopComponentList.SelectedItem = target;
        }
        FocusSelectedTitleEditor();
    }

    internal bool FocusSelectedTitleEditor()
    {
        TextBox? editor = null;
        if (_selectedComponentEntry is { Kind: DesktopComponentKind.Todo, Window: TodoWidgetWindow }
            && BuiltInComponentTitleRow.Visibility == Visibility.Visible)
        {
            editor = SelectedComponentTitleEditor;
        }
        else if (_selectedComponentEntry is { Kind: null, Window: NoteWidgetWindow or FileBoxWindow }
            && SelectedWidgetHeaderBar.Visibility == Visibility.Visible)
        {
            editor = SelectedWidgetTitleEditor;
        }

        if (editor is null)
        {
            return false;
        }

        editor.Focus();
        editor.SelectAll();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!_isClosed && editor.IsVisible)
            {
                editor.Focus();
                editor.SelectAll();
            }
        });
        return true;
    }

    private void HookComponentListScrollViewer()
    {
        if (_componentListScrollViewer is not null || DesktopComponentList is null)
        {
            return;
        }

        if (FindVisualDescendant<ScrollViewer>(DesktopComponentList) is { } viewer)
        {
            _componentListScrollViewer = viewer;
            viewer.ScrollChanged += (_, _) => UpdateComponentListOverflowIndicator();
        }
    }

    private void ScheduleComponentListOverflowUpdate()
    {
        if (_isClosed)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_isClosed)
            {
                return;
            }
            HookComponentListScrollViewer();
            UpdateComponentListOverflowIndicator();
        });
    }

    private void UpdateComponentListOverflowIndicator()
    {
        if (DesktopComponentList is null
            || ComponentListOverflowBar is null
            || ComponentListOverflowFade is null
            || ComponentListOverflowText is null)
        {
            return;
        }

        HookComponentListScrollViewer();
        var hiddenBelow = _componentListScrollViewer is { } viewer
            ? ComponentListScrollRules.ComputeHiddenBelowCount(
                DesktopComponentList.Items.Count,
                viewer.VerticalOffset,
                viewer.ViewportHeight,
                viewer.ExtentHeight,
                viewer.CanContentScroll)
            : 0;

        if (hiddenBelow > 0)
        {
            ComponentListOverflowText.Text = $"↓ {ComponentListScrollRules.FormatOverflowHint(hiddenBelow)}";
            ComponentListOverflowBar.Visibility = Visibility.Visible;
            ComponentListOverflowFade.Visibility = Visibility.Visible;
        }
        else
        {
            ComponentListOverflowText.Text = string.Empty;
            ComponentListOverflowBar.Visibility = Visibility.Collapsed;
            ComponentListOverflowFade.Visibility = Visibility.Collapsed;
        }
    }

    private void ComponentListOverflowBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        HookComponentListScrollViewer();
        if (_componentListScrollViewer is not { } viewer)
        {
            return;
        }

        var step = viewer.CanContentScroll
            ? Math.Max(1d, Math.Floor(viewer.ViewportHeight * 0.75d))
            : Math.Max(48d, viewer.ViewportHeight * 0.75d);
        viewer.ScrollToVerticalOffset(Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + step));
        UpdateComponentListOverflowIndicator();
        e.Handled = true;
    }

    private void ScrollSelectedComponentIntoView(DesktopComponentSettingsEntry entry)
    {
        DesktopComponentList.ScrollIntoView(entry);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!_isClosed && ReferenceEquals(DesktopComponentList.SelectedItem, entry))
            {
                DesktopComponentList.ScrollIntoView(entry);
                UpdateComponentListOverflowIndicator();
            }
        });
    }

    private static T? FindVisualDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }
            if (FindVisualDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }
}
