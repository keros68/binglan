using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BingLan.Core.Models;
using BingLan.Core.TopBar;

namespace BingLan.App.Windows;

public partial class SettingsWindow
{
    private const string TopBarDragDataFormat = "BingLan.TopBarModuleKind";
    private static readonly FontFamily TopBarPreviewIconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private TopBarModuleKind _selectedTopBarModule = TopBarModuleKind.Volume;
    private bool _updatingTopBarVisualEditor;
    private Point _topBarDragStartPoint;
    private TopBarModuleKind? _topBarDragCandidate;

    public void SelectTopBarModule(TopBarModuleKind kind)
    {
        if (!IsTopBarOrderModuleAvailable(kind))
        {
            return;
        }

        _selectedTopBarModule = kind;
        LoadTopBarVisualEditor();
    }

    private void LoadTopBarVisualEditor(TopBarModuleKind? focusModule = null)
    {
        if (_topBarState is not { } state || TopBarVisualLeftHost is null || TopBarVisualRightHost is null)
        {
            return;
        }

        _updatingTopBarVisualEditor = true;
        var pillButtons = new Dictionary<TopBarModuleKind, Button>();
        try
        {
            var preset = TopBarRules.DetectDensityPreset(state);
            TopBarDensityMacRadio.IsChecked = preset == TopBarDensityPreset.MacMinimal;
            TopBarDensityBalancedRadio.IsChecked = preset == TopBarDensityPreset.Balanced;
            TopBarDensityDetailedRadio.IsChecked = preset == TopBarDensityPreset.Detailed;
            TopBarDensityCustomBadge.Visibility = preset is null ? Visibility.Visible : Visibility.Collapsed;

            if (!IsTopBarOrderModuleAvailable(_selectedTopBarModule))
            {
                _selectedTopBarModule = TopBarModuleKind.Volume;
            }

            TopBarVisualLeftHost.Children.Clear();
            TopBarVisualCenterHost.Children.Clear();
            TopBarVisualRightHost.Children.Clear();
            TopBarVisualHiddenHost.Children.Clear();

            var (leftOrder, rightOrder) = TopBarRules.ResolveOrder(state);
            var searchCentered = TopBarRules.EffectiveSearchPlacement(state) == TopBarSearchPlacement.CenterBar;

            foreach (var kind in leftOrder.Where(IsTopBarOrderModuleAvailable))
            {
                if (TopBarRules.IsModuleOn(state.Modules, kind))
                {
                    TopBarVisualLeftHost.Children.Add(BuildVisualModulePill(kind, TopBarModuleSide.Left, enabled: true, out var selectButton));
                    pillButtons[kind] = selectButton;
                }
            }
            if (TopBarVisualLeftHost.Children.Count == 0)
            {
                TopBarVisualLeftHost.Children.Add(BuildZonePlaceholder("左侧（可拖入）"));
            }

            if (searchCentered && TopBarRules.IsModuleOn(state.Modules, TopBarModuleKind.Search))
            {
                TopBarVisualCenterHost.Children.Add(BuildVisualModulePill(TopBarModuleKind.Search, side: null, enabled: true, out var selectButton, centeredBar: true));
                pillButtons[TopBarModuleKind.Search] = selectButton;
            }
            else
            {
                TopBarVisualCenterHost.Children.Add(BuildZonePlaceholder("居中留白（可拖入搜索）"));
            }

            foreach (var kind in rightOrder.Where(IsTopBarOrderModuleAvailable))
            {
                if (TopBarRules.IsModuleOn(state.Modules, kind))
                {
                    TopBarVisualRightHost.Children.Add(BuildVisualModulePill(kind, TopBarModuleSide.Right, enabled: true, out var selectButton));
                    pillButtons[kind] = selectButton;
                }
            }
            if (TopBarVisualRightHost.Children.Count == 0)
            {
                TopBarVisualRightHost.Children.Add(BuildZonePlaceholder("右侧（可拖入）"));
            }

            foreach (var kind in TopBarRules.ModuleOrder.Where(IsTopBarOrderModuleAvailable))
            {
                if (!TopBarRules.IsModuleOn(state.Modules, kind))
                {
                    TopBarVisualHiddenHost.Children.Add(BuildVisualModulePill(kind, side: null, enabled: false, out var selectButton));
                    pillButtons[kind] = selectButton;
                }
            }
            if (TopBarVisualHiddenHost.Children.Count == 0)
            {
                TopBarVisualHiddenHost.Children.Add(BuildZonePlaceholder("全部模块均已在顶栏显示"));
            }

            LoadSelectedTopBarModuleDetails(state);
        }
        finally
        {
            _updatingTopBarVisualEditor = false;
        }

        if (focusModule is { } targetKind && pillButtons.TryGetValue(targetKind, out var focusTarget))
        {
            focusTarget.Focus();
        }
    }

    private TextBlock BuildZonePlaceholder(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 2, 4, 2)
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "SettingsFaintInkBrush");
        return label;
    }

    private Border BuildVisualModulePill(
        TopBarModuleKind kind,
        TopBarModuleSide? side,
        bool enabled,
        out Button selectButton,
        bool centeredBar = false)
    {
        var selected = kind == _selectedTopBarModule;
        var label = TopBarRules.ModuleLabel(kind);
        var compactHint = _topBarState is { } state ? CompactTagForModule(state, kind) : string.Empty;

        var labelContent = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var icon = new TextBlock
        {
            Text = PreviewGlyphForModule(kind),
            FontFamily = TopBarPreviewIconFont,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, selected ? "SettingsCardSelectedTextBrush" : "IceInkBrush");
        labelContent.Children.Add(icon);

        var title = new TextBlock
        {
            Text = centeredBar ? "搜索应用、文件…" : ShortModuleName(kind),
            FontSize = 12,
            FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, selected ? "SettingsCardSelectedTextBrush" : "IceInkBrush");
        labelContent.Children.Add(title);

        if (enabled && !string.IsNullOrEmpty(compactHint))
        {
            var hint = new TextBlock
            {
                Text = $"·{compactHint}",
                FontSize = 10.5,
                Margin = new Thickness(3, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SettingsFaintInkBrush");
            labelContent.Children.Add(hint);
        }

        selectButton = new Button
        {
            Content = labelContent,
            Padding = centeredBar ? new Thickness(10, 3, 4, 3) : new Thickness(7, 3, enabled ? 3 : 7, 3),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Focusable = true
        };
        AutomationProperties.SetName(selectButton, enabled ? $"顶栏{label}胶囊" : $"未启用{label}胶囊");

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(selectButton);

        Button? hideButton = null;
        if (enabled)
        {
            hideButton = new Button
            {
                Content = "×",
                FontSize = 11,
                Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(2, 0, 4, 0),
                MinWidth = 16,
                MinHeight = 16,
                Height = 16,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"隐藏{label}"
            };
            hideButton.SetResourceReference(Button.ForegroundProperty, "SettingsFaintInkBrush");
            AutomationProperties.SetName(hideButton, $"隐藏{label}模块");
            hideButton.Click += (_, args) =>
            {
                args.Handled = true;
                SetTopBarModuleEnabled(kind, false, restoreFocus: true);
            };
            content.Children.Add(hideButton);
        }

        var pill = new Border
        {
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(selected ? 1.5 : 1),
            Padding = new Thickness(0),
            Margin = new Thickness(2.5, 2, 2.5, 2),
            Cursor = Cursors.Hand,
            Opacity = enabled ? 1d : 0.72d,
            AllowDrop = enabled && side.HasValue,
            Tag = (Kind: kind, Side: side),
            Child = content,
            ToolTip = enabled
                ? $"{label}（点击调整样式，拖动可排序或移到另一侧）"
                : $"{label}（点击或拖回上方顶栏即可启用）"
        };
        pill.SetResourceReference(Border.BackgroundProperty, selected ? "SettingsCardSelectedBrush" : "SettingsSurfaceBrush");
        pill.SetResourceReference(Border.BorderBrushProperty, selected ? "SettingsCardSelectedBorderBrush" : "SettingsSurfaceBorderBrush");

        selectButton.Click += (_, args) =>
        {
            args.Handled = true;
            _topBarDragCandidate = null;
            _selectedTopBarModule = kind;
            if (!enabled)
            {
                SetTopBarModuleEnabled(kind, true, restoreFocus: true);
            }
            else
            {
                LoadTopBarVisualEditor(focusModule: kind);
            }
        };

        selectButton.KeyDown += (_, args) =>
        {
            if (enabled && args.Key is Key.Delete or Key.Back)
            {
                args.Handled = true;
                SetTopBarModuleEnabled(kind, false, restoreFocus: true);
            }
        };

        pill.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (hideButton is not null && IsInsideElement(args.OriginalSource as DependencyObject, hideButton))
            {
                return;
            }
            _topBarDragStartPoint = args.GetPosition(this);
            _topBarDragCandidate = kind;
        };

        pill.PreviewMouseMove += (_, args) =>
        {
            if (args.LeftButton != MouseButtonState.Pressed || _topBarDragCandidate != kind)
            {
                return;
            }
            var current = args.GetPosition(this);
            if (Math.Abs(current.X - _topBarDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
                && Math.Abs(current.Y - _topBarDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            {
                return;
            }
            _topBarDragCandidate = null;
            var data = new DataObject(TopBarDragDataFormat, kind);
            DragDrop.DoDragDrop(pill, data, DragDropEffects.Move);
        };

        if (enabled && side is { } targetSide)
        {
            pill.DragOver += (_, args) =>
            {
                if (TryReadDraggedModule(args, out var dragged) && dragged != kind)
                {
                    args.Effects = DragDropEffects.Move;
                    pill.SetResourceReference(Border.BorderBrushProperty, "SettingsCardSelectedBorderBrush");
                }
                else
                {
                    args.Effects = DragDropEffects.None;
                }
                args.Handled = true;
            };
            pill.DragLeave += (_, _) =>
                pill.SetResourceReference(Border.BorderBrushProperty, selected ? "SettingsCardSelectedBorderBrush" : "SettingsSurfaceBorderBrush");
            pill.Drop += (_, args) =>
            {
                args.Handled = true;
                pill.SetResourceReference(Border.BorderBrushProperty, selected ? "SettingsCardSelectedBorderBrush" : "SettingsSurfaceBorderBrush");
                if (TryReadDraggedModule(args, out var dragged) && dragged != kind)
                {
                    DropTopBarModuleBefore(dragged, targetSide, kind);
                }
            };
        }

        return pill;
    }

    private static bool IsInsideElement(DependencyObject? source, DependencyObject ancestor)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private static bool TryReadDraggedModule(DragEventArgs e, out TopBarModuleKind kind)
    {
        if (e.Data.GetDataPresent(TopBarDragDataFormat)
            && e.Data.GetData(TopBarDragDataFormat) is TopBarModuleKind value)
        {
            kind = value;
            return true;
        }
        kind = default;
        return false;
    }

    private void TopBarVisualZone_DragOver(object sender, DragEventArgs e)
    {
        if (TryReadDraggedModule(e, out _))
        {
            e.Effects = DragDropEffects.Move;
            if (sender is Border border)
            {
                border.SetResourceReference(Border.BorderBrushProperty, "SettingsCardSelectedBorderBrush");
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void TopBarVisualCenterZone_DragOver(object sender, DragEventArgs e)
    {
        if (TryReadDraggedModule(e, out var kind) && kind == TopBarModuleKind.Search)
        {
            e.Effects = DragDropEffects.Move;
            if (sender is Border border)
            {
                border.SetResourceReference(Border.BorderBrushProperty, "SettingsCardSelectedBorderBrush");
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void TopBarVisualZone_DragLeave(object sender, DragEventArgs e)
    {
        ResetZoneBorder(sender);
    }

    private void ResetZoneBorder(object sender)
    {
        if (sender is not Border border)
        {
            return;
        }
        if (ReferenceEquals(border, TopBarVisualCenterZone) || ReferenceEquals(border, TopBarVisualHiddenZone))
        {
            border.SetResourceReference(Border.BorderBrushProperty, "SettingsSurfaceBorderBrush");
        }
        else
        {
            border.BorderBrush = Brushes.Transparent;
        }
    }

    private void TopBarVisualLeftZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ResetZoneBorder(sender);
        if (TryReadDraggedModule(e, out var kind))
        {
            DropTopBarModuleToSideEnd(kind, TopBarModuleSide.Left);
        }
    }

    private void TopBarVisualRightZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ResetZoneBorder(sender);
        if (TryReadDraggedModule(e, out var kind))
        {
            DropTopBarModuleToSideEnd(kind, TopBarModuleSide.Right);
        }
    }

    private void TopBarVisualCenterZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ResetZoneBorder(sender);
        if (_topBarState is not { } state || !TryReadDraggedModule(e, out var kind) || kind != TopBarModuleKind.Search)
        {
            return;
        }

        _selectedTopBarModule = TopBarModuleKind.Search;
        state.Modules.Search = true;
        state.SearchPlacement = TopBarSearchPlacement.CenterBar;
        TopBarRules.Normalize(state);
        LoadTopBarVisualEditor();
        LoadTopBarOrderList();
        ApplyTopBarChange("已将“搜索”设回居中搜索条");
    }

    private void TopBarVisualHiddenZone_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        ResetZoneBorder(sender);
        if (TryReadDraggedModule(e, out var kind))
        {
            SetTopBarModuleEnabled(kind, false);
        }
    }

    private void DropTopBarModuleBefore(TopBarModuleKind dragged, TopBarModuleSide targetSide, TopBarModuleKind anchor)
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _selectedTopBarModule = dragged;
        TopBarRules.SetModule(state.Modules, dragged, true);
        if (dragged == TopBarModuleKind.Search)
        {
            state.SearchPlacement = TopBarSearchPlacement.SideIcon;
        }

        var (left, right) = TopBarRules.ResolveOrder(state);
        var list = (targetSide == TopBarModuleSide.Left ? left : right).ToList();
        list.Remove(dragged);
        var anchorIndex = list.IndexOf(anchor);
        var insertIndex = anchorIndex >= 0 ? anchorIndex : list.Count;

        TopBarRules.PlaceModule(state, dragged, targetSide, insertIndex);
        LoadTopBarVisualEditor();
        LoadTopBarOrderList();
        ApplyTopBarChange($"已调整“{TopBarRules.ModuleLabel(dragged)}”位置");
    }

    private void DropTopBarModuleToSideEnd(TopBarModuleKind dragged, TopBarModuleSide targetSide)
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _selectedTopBarModule = dragged;
        TopBarRules.SetModule(state.Modules, dragged, true);
        TopBarRules.PlaceModule(state, dragged, targetSide, int.MaxValue);
        LoadTopBarVisualEditor();
        LoadTopBarOrderList();
        var sideText = targetSide == TopBarModuleSide.Left ? "左侧" : "右侧";
        ApplyTopBarChange($"已将“{TopBarRules.ModuleLabel(dragged)}”放到{sideText}");
    }

    private void SetTopBarModuleEnabled(TopBarModuleKind kind, bool enabled, bool restoreFocus = false)
    {
        if (_topBarState is not { } state)
        {
            return;
        }

        _selectedTopBarModule = kind;
        TopBarRules.SetModule(state.Modules, kind, enabled);
        LoadTopBarVisualEditor(focusModule: restoreFocus ? kind : null);
        LoadTopBarOrderList();
        ApplyTopBarChange(enabled
            ? $"已显示“{TopBarRules.ModuleLabel(kind)}”"
            : $"已隐藏“{TopBarRules.ModuleLabel(kind)}”");
    }

    private void LoadSelectedTopBarModuleDetails(TopBarState state)
    {
        var kind = _selectedTopBarModule;
        var label = TopBarRules.ModuleLabel(kind);
        var enabled = TopBarRules.IsModuleOn(state.Modules, kind);
        var searchCentered = kind == TopBarModuleKind.Search
            && TopBarRules.EffectiveSearchPlacement(state) == TopBarSearchPlacement.CenterBar;

        TopBarSelectedModuleTitle.Text = label;
        TopBarSelectedModuleSummary.Text = !enabled
            ? "当前已隐藏；开启开关或拖回上方顶栏即可显示"
            : searchCentered
                ? "当前位于顶栏居中位置"
                : BuildLocationSummary(state, kind);
        TopBarSelectedModuleEnabledCheckBox.IsChecked = enabled;

        var orderable = enabled && !searchCentered;
        if (orderable)
        {
            var (left, right) = TopBarRules.ResolveOrder(state);
            var onLeft = left.Contains(kind);
            var sideList = (onLeft ? left : right).Where(IsTopBarOrderModuleAvailable).ToArray();
            var idx = Array.IndexOf(sideList, kind);
            TopBarSelectedMoveLeftButton.Visibility = Visibility.Visible;
            TopBarSelectedMoveRightButton.Visibility = Visibility.Visible;
            TopBarSelectedSwitchSideButton.Visibility = Visibility.Visible;
            TopBarSelectedMoveLeftButton.IsEnabled = idx > 0;
            TopBarSelectedMoveRightButton.IsEnabled = idx >= 0 && idx < sideList.Length - 1;
            TopBarSelectedSwitchSideButton.Content = onLeft ? "移到右侧" : "移到左侧";
            TopBarSelectedSwitchSideButton.Tag = onLeft ? TopBarModuleSide.Right : TopBarModuleSide.Left;
        }
        else
        {
            TopBarSelectedMoveLeftButton.Visibility = Visibility.Collapsed;
            TopBarSelectedMoveRightButton.Visibility = Visibility.Collapsed;
            TopBarSelectedSwitchSideButton.Visibility = Visibility.Collapsed;
        }

        TopBarSelectedStyleRow.Visibility = Visibility.Collapsed;
        TopBarSelectedSearchPanel.Visibility = Visibility.Collapsed;
        TopBarSelectedModuleNote.Visibility = Visibility.Collapsed;
        TopBarSelectedModuleStylePanel.IsEnabled = enabled;

        switch (kind)
        {
            case TopBarModuleKind.Volume:
                PopulateStyleCombo(
                    "音量显示方式",
                    "静音时始终仅显示静音图标，悬停可查看精确百分比",
                    [
                        ("仅图标（Mac 风格）", "IconOnly"),
                        ("图标 + 百分比（静音仅图标）", "IconAndPercent")
                    ],
                    TopBarRules.EffectiveVolumeStyle(state).ToString());
                break;
            case TopBarModuleKind.Network:
                PopulateStyleCombo(
                    "网络显示方式",
                    "悬停始终可查看完整网络名称与信号强度；录屏时建议保持仅图标或 WLAN",
                    [
                        ("仅图标（Mac 风格）", "IconOnly"),
                        ("图标 + WLAN", "IconAndWlan"),
                        ("图标 + Wi-Fi 名称", "IconAndWifiName")
                    ],
                    TopBarRules.EffectiveNetworkDisplayMode(state).ToString());
                break;
            case TopBarModuleKind.Battery:
                PopulateStyleCombo(
                    "电量显示方式",
                    "电池图标自动反映电量格数与充电状态，悬停显示完整信息",
                    [
                        ("仅图标（Mac 风格）", "IconOnly"),
                        ("图标 + 百分比", "IconAndPercent")
                    ],
                    TopBarRules.EffectiveBatteryStyle(state).ToString());
                break;
            case TopBarModuleKind.InputMethod:
                PopulateStyleCombo(
                    "输入法显示方式",
                    "点击弹出轻量输入法切换浮层，悬停可查看完整输入法名称",
                    [
                        ("仅中/英徽标（Mac 风格）", "BadgeOnly"),
                        ("徽标 + 输入法名称", "BadgeAndName")
                    ],
                    TopBarRules.EffectiveInputMethodStyle(state).ToString());
                break;
            case TopBarModuleKind.Weather:
                PopulateStyleCombo(
                    "天气显示方式",
                    "悬停始终可查看城市、今日高低温、湿度与更新时间",
                    [
                        ("简洁（仅温度）", "Compact"),
                        ("完整（城市 + 温度 + 天气）", "Full")
                    ],
                    TopBarRules.EffectiveWeatherStyle(state).ToString());
                break;
            case TopBarModuleKind.Performance:
                PopulateStyleCombo(
                    "性能显示方式",
                    "点击打开任务管理器性能页，悬停查看完整 CPU、内存与上下行速率",
                    [
                        ("精简（CPU + 下载速率）", "Compact"),
                        ("完整（CPU / 内存 / 上下行）", "Full")
                    ],
                    TopBarRules.EffectivePerformanceStyle(state).ToString());
                break;
            case TopBarModuleKind.TrayOverflow:
                PopulateStyleCombo(
                    "系统托盘显示方式",
                    "点击打开 Windows 11 隐藏的托盘图标面板",
                    [
                        ("仅图标（Mac 风格）", "IconOnly"),
                        ("图标 + “托盘”文字", "IconAndLabel")
                    ],
                    TopBarRules.EffectiveTrayIconOnly(state) ? "IconOnly" : "IconAndLabel");
                break;
            case TopBarModuleKind.Search:
                TopBarSelectedSearchPanel.Visibility = Visibility.Visible;
                SelectComboByTag(TopBarSearchPlacementComboBox, TopBarRules.EffectiveSearchPlacement(state).ToString());
                TopBarSearchInlineCheckBox.IsChecked = state.SearchInline == true;
                SelectTopBarSearchEngine(state.SearchEngine);
                TopBarSearchCustomUrlTextBox.Text = state.SearchCustomUrl ?? string.Empty;
                SetTopBarSearchCustomUrlError(null);
                UpdateTopBarSearchEngineUi(state);
                break;
            case TopBarModuleKind.Clock:
                TopBarSelectedModuleNote.Text = "时钟的 12/24 小时制、日期与农历显示跟随桌面时间日期设置；点击顶栏时钟可打开月历。";
                TopBarSelectedModuleNote.Visibility = Visibility.Visible;
                break;
            case TopBarModuleKind.TodoSummary:
                TopBarSelectedModuleNote.Text = "显示今日未完成待办计数，点击弹出待办清单浮层可直接勾选完成。";
                TopBarSelectedModuleNote.Visibility = Visibility.Visible;
                break;
            case TopBarModuleKind.Attention:
                TopBarSelectedModuleNote.Text = "当有应用闪烁请求注意时自动出现（最多 3 个），平时不占顶栏空间。";
                TopBarSelectedModuleNote.Visibility = Visibility.Visible;
                break;
        }
    }

    private void PopulateStyleCombo(string header, string description, (string Label, string Tag)[] items, string selectedTag)
    {
        TopBarSelectedStyleHeader.Text = header;
        TopBarSelectedStyleDesc.Text = description;
        TopBarSelectedStyleComboBox.Items.Clear();
        foreach (var (itemLabel, tag) in items)
        {
            TopBarSelectedStyleComboBox.Items.Add(new ComboBoxItem { Content = itemLabel, Tag = tag });
        }
        SelectComboByTag(TopBarSelectedStyleComboBox, selectedTag);
        TopBarSelectedStyleRow.Visibility = Visibility.Visible;
    }

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var entry in combo.Items)
        {
            if (entry is ComboBoxItem { Tag: string itemTag } item
                && string.Equals(itemTag, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        if (combo.Items.Count > 0)
        {
            combo.SelectedIndex = 0;
        }
    }

    private void TopBarDensityPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBar || _updatingTopBarVisualEditor || _topBarState is not { } state)
        {
            return;
        }

        TopBarDensityPreset preset;
        string name;
        if (ReferenceEquals(sender, TopBarDensityMacRadio))
        {
            preset = TopBarDensityPreset.MacMinimal;
            name = "Mac 极简";
        }
        else if (ReferenceEquals(sender, TopBarDensityBalancedRadio))
        {
            preset = TopBarDensityPreset.Balanced;
            name = "均衡";
        }
        else
        {
            preset = TopBarDensityPreset.Detailed;
            name = "信息流";
        }

        TopBarRules.ApplyDensityPreset(state, preset);
        TopBarRules.Normalize(state);
        LoadTopBarVisualEditor();
        LoadTopBarOrderList();
        ApplyTopBarChange($"已切换顶栏为“{name}”风格");
    }

    private void TopBarSelectedModuleEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBar || _updatingTopBarVisualEditor || _topBarState is not { } state)
        {
            return;
        }

        SetTopBarModuleEnabled(_selectedTopBarModule, TopBarSelectedModuleEnabledCheckBox.IsChecked == true);
    }

    private void TopBarSelectedMoveLeft_Click(object sender, RoutedEventArgs e) =>
        MoveTopBarModule(_selectedTopBarModule, -1);

    private void TopBarSelectedMoveRight_Click(object sender, RoutedEventArgs e) =>
        MoveTopBarModule(_selectedTopBarModule, 1);

    private void TopBarSelectedSwitchSide_Click(object sender, RoutedEventArgs e)
    {
        if (TopBarSelectedSwitchSideButton.Tag is TopBarModuleSide targetSide)
        {
            MoveTopBarModuleSide(_selectedTopBarModule, targetSide);
        }
    }

    private void TopBarSelectedStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingTopBar || _updatingTopBarVisualEditor || _topBarState is not { } state
            || TopBarSelectedStyleComboBox.SelectedItem is not ComboBoxItem { Tag: string tag })
        {
            return;
        }

        switch (_selectedTopBarModule)
        {
            case TopBarModuleKind.Volume:
                state.VolumeStyle = tag == "IconOnly" ? TopBarVolumeStyle.IconOnly : TopBarVolumeStyle.IconAndPercent;
                break;
            case TopBarModuleKind.Network:
                var netMode = tag switch
                {
                    "IconOnly" => TopBarNetworkDisplayMode.IconOnly,
                    "IconAndWifiName" => TopBarNetworkDisplayMode.IconAndWifiName,
                    _ => TopBarNetworkDisplayMode.IconAndWlan
                };
                TopBarRules.SetNetworkDisplayMode(state, netMode);
                break;
            case TopBarModuleKind.Battery:
                state.BatteryStyle = tag == "IconOnly" ? TopBarBatteryStyle.IconOnly : TopBarBatteryStyle.IconAndPercent;
                break;
            case TopBarModuleKind.InputMethod:
                state.InputMethodStyle = tag == "BadgeOnly" ? TopBarInputMethodStyle.BadgeOnly : TopBarInputMethodStyle.BadgeAndName;
                break;
            case TopBarModuleKind.Weather:
                state.WeatherStyle = tag == "Compact" ? TopBarWeatherStyle.Compact : TopBarWeatherStyle.Full;
                break;
            case TopBarModuleKind.Performance:
                state.PerformanceStyle = tag == "Compact" ? TopBarPerformanceStyle.Compact : TopBarPerformanceStyle.Full;
                break;
            case TopBarModuleKind.TrayOverflow:
                state.TrayIconOnly = tag == "IconOnly";
                break;
        }

        LoadTopBarVisualEditor();
        ApplyTopBarChange($"已更新“{TopBarRules.ModuleLabel(_selectedTopBarModule)}”显示样式");
    }

    private void TopBarSearchOptions_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingTopBar || _updatingTopBarVisualEditor || _topBarState is not { } state)
        {
            return;
        }

        if (TopBarSearchPlacementComboBox.SelectedItem is ComboBoxItem { Tag: string placementTag })
        {
            state.SearchPlacement = placementTag == "SideIcon"
                ? TopBarSearchPlacement.SideIcon
                : TopBarSearchPlacement.CenterBar;
        }
        state.SearchInline = TopBarSearchInlineCheckBox.IsChecked == true;
        if (TopBarSearchEngineComboBox.SelectedItem is ComboBoxItem { Tag: string engineKey })
        {
            var normalizedEngine = TopBarSearchRowRules.NormalizeEngineKey(engineKey);
            state.SearchEngine = normalizedEngine == TopBarSearchRowRules.EngineBing ? null : normalizedEngine;
        }
        TopBarRules.Normalize(state);
        LoadTopBarVisualEditor();
        LoadTopBarOrderList();
        ApplyTopBarChange("已更新搜索入口设置");
    }

    private static string BuildLocationSummary(TopBarState state, TopBarModuleKind kind)
    {
        var (left, _) = TopBarRules.ResolveOrder(state);
        return left.Contains(kind) ? "当前位于左侧区域（可拖拽调整顺序）" : "当前位于右侧区域（可拖拽调整顺序）";
    }

    private static string ShortModuleName(TopBarModuleKind kind) => kind switch
    {
        TopBarModuleKind.TodoSummary => "待办",
        TopBarModuleKind.Weather => "天气",
        TopBarModuleKind.Performance => "性能",
        TopBarModuleKind.Attention => "消息",
        TopBarModuleKind.InputMethod => "输入法",
        TopBarModuleKind.Volume => "音量",
        TopBarModuleKind.Network => "网络",
        TopBarModuleKind.Battery => "电量",
        TopBarModuleKind.Clock => "时钟",
        TopBarModuleKind.Search => "搜索",
        TopBarModuleKind.TrayOverflow => "托盘",
        _ => kind.ToString()
    };

    private static string PreviewGlyphForModule(TopBarModuleKind kind) => kind switch
    {
        TopBarModuleKind.TodoSummary => "\uE73E",
        TopBarModuleKind.Weather => "\uE706",
        TopBarModuleKind.Performance => "\uE9D9",
        TopBarModuleKind.Attention => "\uEA8F",
        TopBarModuleKind.InputMethod => "\uE92E",
        TopBarModuleKind.Volume => "\uE767",
        TopBarModuleKind.Network => "\uE701",
        TopBarModuleKind.Battery => "\uE83F",
        TopBarModuleKind.Clock => "\uE823",
        TopBarModuleKind.Search => "\uE721",
        TopBarModuleKind.TrayOverflow => "\uE70E",
        _ => "\uE713"
    };

    private static string CompactTagForModule(TopBarState state, TopBarModuleKind kind) => kind switch
    {
        TopBarModuleKind.Volume when TopBarRules.EffectiveVolumeStyle(state) == TopBarVolumeStyle.IconOnly => "图标",
        TopBarModuleKind.Network when TopBarRules.EffectiveNetworkDisplayMode(state) == TopBarNetworkDisplayMode.IconOnly => "图标",
        TopBarModuleKind.Battery when TopBarRules.EffectiveBatteryStyle(state) == TopBarBatteryStyle.IconOnly => "图标",
        TopBarModuleKind.InputMethod when TopBarRules.EffectiveInputMethodStyle(state) == TopBarInputMethodStyle.BadgeOnly => "徽标",
        TopBarModuleKind.Weather when TopBarRules.EffectiveWeatherStyle(state) == TopBarWeatherStyle.Compact => "精简",
        TopBarModuleKind.Performance when TopBarRules.EffectivePerformanceStyle(state) == TopBarPerformanceStyle.Compact => "精简",
        TopBarModuleKind.TrayOverflow when TopBarRules.EffectiveTrayIconOnly(state) => "图标",
        TopBarModuleKind.Search when TopBarRules.EffectiveSearchPlacement(state) == TopBarSearchPlacement.SideIcon => "图标",
        _ => string.Empty
    };
}
